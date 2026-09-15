using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IlRepl.Host;

/// <summary>
/// Owns a Windows job handle whose final close terminates any remaining comparison descendants.
/// </summary>
internal sealed partial class ComparisonJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Creates the empty handle required by native handle marshalling.
    /// </summary>
    public ComparisonJobHandle() : base(ownsHandle: true)
    {
    }

    /// <summary>
    /// Releases the native job and its kill-on-close ownership.
    /// </summary>
    /// <returns>Whether the operating system closed the handle.</returns>
    protected override bool ReleaseHandle() => CloseHandle(handle) != 0;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(nint handle);
}
