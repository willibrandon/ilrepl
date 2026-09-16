using System.ComponentModel;
using System.Diagnostics;

namespace IlRepl.Tui;

/// <summary>
/// Opens instruction documentation through the terminal host's configured web browser.
/// </summary>
internal static class DocumentationLauncher
{
    /// <summary>
    /// Attempts to open a documentation URL, preserving the visible URL when no browser is available.
    /// </summary>
    internal static void Open(string url)
    {
        if (OperatingSystem.IsBrowser() || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http"))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            // The complete URL remains visible and copyable in the help view.
        }
    }
}
