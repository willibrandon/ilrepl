using System.Runtime.InteropServices;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Observes the real worker's native crash policy and raises a fatal Windows exception without writing diagnostics.
/// </summary>
public static partial class WindowsFatalSource
{
    /// <summary>
    /// Reports whether the current worker bypasses Windows Error Reporting for fatal exceptions.
    /// </summary>
    /// <returns>Whether the current native process error mode includes SEM_NOGPFAULTERRORBOX.</returns>
    public static bool HandlesOwnCrashes() => (GetErrorMode() & 0x2) != 0;

    /// <summary>
    /// Ends the current worker through the native fail-fast path without producing a managed diagnostic report.
    /// </summary>
    public static void FailFast() => RaiseFailFastException(0, 0, 0);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    private static partial uint GetErrorMode();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    private static partial void RaiseFailFastException(nint exception, nint context, uint flags);
}
