using System.Runtime.InteropServices;

namespace IlRepl.Host;

/// <summary>
/// The native Windows extended job limits, including kill-on-close ownership.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ComparisonJobExtendedLimits
{
    /// <summary>
    /// The basic job limits.
    /// </summary>
    internal ComparisonJobLimits Basic;

    /// <summary>
    /// The reserved I/O counters.
    /// </summary>
    internal ComparisonJobIo Io;

    /// <summary>
    /// The process memory limit.
    /// </summary>
    internal nuint ProcessMemory;

    /// <summary>
    /// The job memory limit.
    /// </summary>
    internal nuint JobMemory;

    /// <summary>
    /// The peak process memory usage.
    /// </summary>
    internal nuint PeakProcessMemory;

    /// <summary>
    /// The peak job memory usage.
    /// </summary>
    internal nuint PeakJobMemory;
}
