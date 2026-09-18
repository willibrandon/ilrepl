using System.Runtime.InteropServices;

namespace IlRepl.Protocol;

/// <summary>
/// The native Windows job accounting used to await descendant termination.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OwnedJobAccounting
{
    /// <summary>
    /// The total user time.
    /// </summary>
    internal long UserTime;

    /// <summary>
    /// The total kernel time.
    /// </summary>
    internal long KernelTime;

    /// <summary>
    /// The user time in this accounting period.
    /// </summary>
    internal long PeriodUserTime;

    /// <summary>
    /// The kernel time in this period.
    /// </summary>
    internal long PeriodKernelTime;

    /// <summary>
    /// The page fault count.
    /// </summary>
    internal uint PageFaults;

    /// <summary>
    /// The total assigned process count.
    /// </summary>
    internal uint TotalProcesses;

    /// <summary>
    /// The processes that have not finished terminating.
    /// </summary>
    internal uint ActiveProcesses;

    /// <summary>
    /// The processes terminated by a job limit.
    /// </summary>
    internal uint TerminatedProcesses;
}
