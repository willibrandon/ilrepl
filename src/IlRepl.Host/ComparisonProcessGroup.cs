using System.ComponentModel;
using System.Diagnostics;
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
    /// <returns>A task that completes when the owned process group or job is empty.</returns>
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
            if (SignalGroup(-_group, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var wait = Stopwatch.StartNew();
            while (SignalGroup(-_group, 0) == 0)
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("comparison descendants did not terminate");
                await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
            }

            // ESRCH confirms that no process remains in the group.
            if (Marshal.GetLastPInvokeError() != 3) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        _stopped = true;
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
