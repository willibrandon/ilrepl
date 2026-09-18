using System.Runtime.InteropServices;

namespace IlRepl.Protocol;

/// <summary>
/// The native Windows job I/O counters embedded in extended limits.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OwnedJobIo
{
    /// <summary>
    /// The read operation count.
    /// </summary>
    internal ulong ReadOperations;

    /// <summary>
    /// The write operation count.
    /// </summary>
    internal ulong WriteOperations;

    /// <summary>
    /// The other operation count.
    /// </summary>
    internal ulong OtherOperations;

    /// <summary>
    /// The bytes read.
    /// </summary>
    internal ulong ReadBytes;

    /// <summary>
    /// The bytes written.
    /// </summary>
    internal ulong WriteBytes;

    /// <summary>
    /// The bytes transferred by other operations.
    /// </summary>
    internal ulong OtherBytes;
}
