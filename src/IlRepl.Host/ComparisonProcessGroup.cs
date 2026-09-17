using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IlRepl.Host;

/// <summary>
/// Retains ownership of comparison descendants after their worker process has exited.
/// </summary>
internal sealed partial class ComparisonProcessGroup : IDisposable
{
    private ComparisonJobHandle? _job;
    private int _group;
    private bool _stopped;

    /// <summary>
    /// Creates a separate Unix session before the worker is allowed to execute compared code.
    /// </summary>
    internal static void PrepareWorker()
    {
        if (!OperatingSystem.IsWindows() && CreateSession() < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// Takes ownership of a worker that has completed process-group setup and is waiting for permission to run.
    /// </summary>
    /// <param name="process">The started comparison worker.</param>
    internal void Attach(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            _group = process.Id;
            return;
        }

        _job = CreateJobObjectW(0, 0);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var limits = new ComparisonJobExtendedLimits
        {
            Basic = new ComparisonJobLimits { Flags = 0x2000 }, // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };
        if (SetInformationJobObject(_job, 9, in limits, (uint)Marshal.SizeOf<ComparisonJobExtendedLimits>()) == 0
            || AssignProcessToJobObject(_job, process.SafeHandle) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// Terminates the owned group even when its original worker is no longer running.
    /// </summary>
    /// <returns>A task that completes when the owned process group or job has no executing processes.</returns>
    internal async Task StopAsync()
    {
        if (_stopped) return;
        if (_job is { IsInvalid: false } job)
        {
            if (TerminateJobObject(job, 1) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var wait = Stopwatch.StartNew();
            while (true)
            {
                if (QueryInformationJobObject(job, 1, out var accounting,
                    (uint)Marshal.SizeOf<ComparisonJobAccounting>(), 0) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                if (accounting.ActiveProcesses == 0) break;
                if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("comparison descendants did not terminate");
                await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
            }
        }
        else if (_group != 0)
        {
            var signalled = SignalGroup(-_group, 9);
            var error = signalled == 0 ? 0 : Marshal.GetLastPInvokeError();
            if (error != 0 && error != 3 && !(OperatingSystem.IsMacOS() && error == 1))
            {
                throw new Win32Exception(error);
            }

            var wait = Stopwatch.StartNew();
            while (true)
            {
                var exists = SignalGroup(-_group, 0);
                error = exists == 0 ? 0 : Marshal.GetLastPInvokeError();
                if (error == 3) break; // ESRCH confirms that the group is empty.
                if (OperatingSystem.IsLinux() && !HasLiveLinuxMembers(_group)) break;
                // Darwin can return EPERM while an exited group's zombies await reaping.
                if (error != 0 && !(OperatingSystem.IsMacOS() && error == 1)) throw new Win32Exception(error);
                if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("comparison descendants did not terminate");
                await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
            }
        }

        _stopped = true;
    }

    private static bool HasLiveLinuxMembers(int group)
    {
        // A container's PID 1 may not reap adopted children. Zombies cannot execute or hold a pipe open,
        // but kill(group, 0) still reports their group as existing; only their parent can reap them.
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
            try
            {
                var status = File.ReadAllText(Path.Combine(directory, "stat"));
                var closing = status.LastIndexOf(')');
                if (closing < 0 || closing + 2 >= status.Length) return true;
                var fields = status[(closing + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3) return true;
                if (int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var owner)
                    && owner == group && fields[0] is not ("Z" or "X")) return true;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // The process exited while the snapshot was being read.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return true; // Missing evidence must not be mistaken for completed cleanup.
            }
        }
        return false;
    }

    /// <summary>
    /// Closes the Windows job handle after explicit group cleanup.
    /// </summary>
    public void Dispose() => _job?.Dispose();

    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static partial int CreateSession();

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SignalGroup(int group, int signal);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial ComparisonJobHandle CreateJobObjectW(nint attributes, nint name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetInformationJobObject(ComparisonJobHandle job, int informationClass,
        in ComparisonJobExtendedLimits information, uint length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AssignProcessToJobObject(ComparisonJobHandle job, SafeProcessHandle process);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int TerminateJobObject(ComparisonJobHandle job, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int QueryInformationJobObject(ComparisonJobHandle job, int informationClass,
        out ComparisonJobAccounting information, uint length, nint returnedLength);
}
