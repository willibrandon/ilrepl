using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IlRepl.Protocol;

/// <summary>
/// Retains ownership of owned process descendants after their worker process has exited.
/// </summary>
public sealed partial class OwnedProcessGroup : IDisposable
{
    private OwnedJobHandle? _job;
    private int _group;
    private bool _stopped;

    /// <summary>
    /// Creates a separate Unix session before the worker is allowed to execute user code.
    /// </summary>
    public static void PrepareWorker()
    {
        if (!OperatingSystem.IsWindows() && CreateSession() < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// Takes ownership of a worker that has completed process-group setup and is waiting for permission to run.
    /// </summary>
    /// <param name="process">The started owned process worker.</param>
    public void Attach(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            _group = process.Id;
            return;
        }

        _job = CreateJobObjectW(0, 0);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var limits = new OwnedJobExtendedLimits
        {
            Basic = new OwnedJobLimits { Flags = 0x2000 }, // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };
        if (SetInformationJobObject(_job, 9, in limits, (uint)Marshal.SizeOf<OwnedJobExtendedLimits>()) == 0
            || AssignProcessToJobObject(_job, process.SafeHandle) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// Retains an already established Unix group during supervisor adoption.
    /// </summary>
    /// <param name="group">The acknowledged process group identifier.</param>
    public void Adopt(int group)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _group = group;
    }

    /// <summary>
    /// Captures a process identity before releasing its startup handshake.
    /// </summary>
    /// <param name="process">The prepared process.</param>
    /// <param name="identity">The scope identity.</param>
    /// <param name="parent">The owning runtime identity.</param>
    /// <returns>A stable identity for the ownership registry.</returns>
    public static OwnedProcessScope Describe(Process process, string identity, string? parent = null) =>
        new(identity, process.Id, GetStartIdentity(process), parent);

    /// <summary>
    /// Reads the kernel's stable process creation identity without a per-process wall-clock conversion on Linux.
    /// </summary>
    /// <param name="process">The process to identify.</param>
    /// <returns>Linux clock ticks since boot, or the native creation timestamp on other platforms.</returns>
    public static long GetStartIdentity(Process process)
    {
        if (!OperatingSystem.IsLinux()) return process.StartTime.ToUniversalTime().Ticks;
        var status = File.ReadAllText("/proc/" + process.Id.ToString(CultureInfo.InvariantCulture) + "/stat");
        var fields = status[(status.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return long.Parse(fields[19], CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rejects process identifier reuse while retaining groups with surviving descendants.
    /// </summary>
    /// <param name="scope">The previously acknowledged scope.</param>
    /// <returns>Whether the same process or its surviving group still exists.</returns>
    public static bool IsCurrent(OwnedProcessScope scope)
    {
        try
        {
            using var process = Process.GetProcessById(scope.ProcessId);
            if (GetStartIdentity(process) != scope.StartIdentity) return false;
            if (!OperatingSystem.IsWindows() && GetProcessGroup(scope.ProcessId) != scope.ProcessId) return false;
            return !(OperatingSystem.IsWindows() ? process.WaitForExit(0) : process.HasExited) || GroupExists(scope.ProcessId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or FileNotFoundException or DirectoryNotFoundException
            || exception is Win32Exception && OperatingSystem.IsMacOS()
            || exception is IOException && OperatingSystem.IsLinux() && (exception.HResult & 0xffff) is 2 or 3)
        {
            return GroupExists(scope.ProcessId);
        }
    }

    /// <summary>
    /// Checks whether the original process can still execute, excluding unreaped Unix zombies and reused identifiers.
    /// </summary>
    /// <param name="scope">The stable process identity.</param>
    /// <returns>Whether that exact process is still executing.</returns>
    public static bool IsRunning(OwnedProcessScope scope)
    {
        try
        {
            using var process = Process.GetProcessById(scope.ProcessId);
            if (GetStartIdentity(process) != scope.StartIdentity) return false;
            if (OperatingSystem.IsWindows()) return !process.WaitForExit(0);
            if (process.HasExited) return false;
            if (!OperatingSystem.IsLinux()) return true;
            var status = File.ReadAllText("/proc/" + scope.ProcessId.ToString(CultureInfo.InvariantCulture) + "/stat");
            var closing = status.LastIndexOf(')');
            return closing < 0 || closing + 2 >= status.Length || status[closing + 2] is not ('Z' or 'X');
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or FileNotFoundException or DirectoryNotFoundException
            || exception is Win32Exception && OperatingSystem.IsMacOS()
            || exception is IOException && OperatingSystem.IsLinux() && (exception.HResult & 0xffff) is 2 or 3)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for an adopted process to stop even when an unrelated container init process has not reaped its zombie.
    /// </summary>
    /// <param name="scope">The stable process identity.</param>
    /// <param name="cancellationToken">Cancels the observation.</param>
    /// <returns>Completion once the process cannot execute.</returns>
    public static async Task WaitForExitAsync(OwnedProcessScope scope, CancellationToken cancellationToken)
    {
        while (IsRunning(scope)) await Task.Delay(10, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a retained child process handle to signal completion before callers release its filesystem resources.
    /// </summary>
    /// <param name="process">The original started process with its retained operating-system handle.</param>
    /// <param name="cancellationToken">Cancels the observation without terminating the process.</param>
    /// <returns>Completion after process termination and redirected event-output draining.</returns>
    public static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        // On Windows HasExited can observe the exit code before the process object is signalled.
        // WaitForExitAsync uses that shortcut too; a kernel wait is the resource-release boundary.
        if (OperatingSystem.IsWindows())
        {
            while (!process.WaitForExit(0)) await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Uses the retained Windows process handle or the adopted Unix identity to observe complete termination.
    /// </summary>
    /// <param name="process">The retained process object.</param>
    /// <param name="scope">The stable identity used for an adopted Unix process.</param>
    /// <param name="cancellationToken">Cancels the observation without terminating the process.</param>
    /// <returns>Completion after termination, including unreaped adopted Unix zombies.</returns>
    public static Task WaitForExitAsync(Process process, OwnedProcessScope scope, CancellationToken cancellationToken) =>
        OperatingSystem.IsWindows() ? WaitForExitAsync(process, cancellationToken) : WaitForExitAsync(scope, cancellationToken);

    private static bool GroupExists(int group)
    {
        if (OperatingSystem.IsWindows()) return false;
        if (SignalGroup(-group, 0) != 0 && Marshal.GetLastPInvokeError() == 3) return false;
        return !OperatingSystem.IsLinux() || HasLiveLinuxMembers(group);
    }

    [LibraryImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static partial int GetProcessGroup(int process);

    /// <summary>
    /// Terminates the owned group even when its original worker is no longer running.
    /// </summary>
    /// <returns>A task that completes when the owned process group or job has no executing processes.</returns>
    public async Task StopAsync()
    {
        if (_stopped) return;
        if (_job is { IsInvalid: false } job)
        {
            if (TerminateJobObject(job, 1) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var wait = Stopwatch.StartNew();
            while (true)
            {
                if (QueryInformationJobObject(job, 1, out var accounting,
                    (uint)Marshal.SizeOf<OwnedJobAccounting>(), 0) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                if (accounting.ActiveProcesses == 0) break;
                if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("owned process descendants did not terminate");
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
                if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("owned process descendants did not terminate");
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
    private static partial OwnedJobHandle CreateJobObjectW(nint attributes, nint name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetInformationJobObject(OwnedJobHandle job, int informationClass,
        in OwnedJobExtendedLimits information, uint length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AssignProcessToJobObject(OwnedJobHandle job, SafeProcessHandle process);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int TerminateJobObject(OwnedJobHandle job, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int QueryInformationJobObject(OwnedJobHandle job, int informationClass,
        out OwnedJobAccounting information, uint length, nint returnedLength);
}
