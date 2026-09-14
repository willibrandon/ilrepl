using System.Runtime.InteropServices;

namespace IlRepl.Host;

/// <summary>
/// The native Windows job limits used to keep descendants in the comparison lifetime.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ComparisonJobLimits
{
    /// <summary>
    /// The per-process user time limit.
    /// </summary>
    internal long ProcessUserTime;

    /// <summary>
    /// The job user time limit.
    /// </summary>
    internal long JobUserTime;

    /// <summary>
    /// The enabled job limit flags.
    /// </summary>
    internal uint Flags;

    /// <summary>
    /// The minimum working set size.
    /// </summary>
    internal nuint MinimumWorkingSet;

    /// <summary>
    /// The maximum working set size.
    /// </summary>
    internal nuint MaximumWorkingSet;

    /// <summary>
    /// The active process limit.
    /// </summary>
    internal uint ActiveProcessLimit;

    /// <summary>
    /// The processor affinity mask.
    /// </summary>
    internal nuint Affinity;

    /// <summary>
    /// The process priority class.
    /// </summary>
    internal uint Priority;

    /// <summary>
    /// The scheduling class.
    /// </summary>
    internal uint Scheduling;
}
