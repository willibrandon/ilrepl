using System.ComponentModel;
using System.Diagnostics;
using Hex1b;

namespace IlRepl.Tui;

/// <summary>
/// Puts copied text on the clipboard. The terminal is always asked through OSC 52, which the
/// docs page and most terminals honor. On a desktop the platform's clipboard command is used as
/// well, for terminals that do not.
/// </summary>
public static class ClipboardWriter
{
    /// <summary>
    /// Copies text through the terminal and, when asked, through the platform clipboard too.
    /// </summary>
    /// <param name="app">The running app, which writes the OSC 52 sequence.</param>
    /// <param name="text">The text to copy.</param>
    /// <param name="usePlatformClipboard">True to also use the platform's clipboard command.</param>
    public static void Copy(Hex1bApp app, string text, bool usePlatformClipboard)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(text);
        app.CopyToClipboard(text);
        if (usePlatformClipboard && !OperatingSystem.IsBrowser())
        {
            TryPlatformCopy(text);
        }
    }

    /// <summary>
    /// Runs the platform's clipboard command with the text on its standard input. A missing
    /// command or a failure is ignored; the OSC 52 path has already been tried.
    /// </summary>
    /// <param name="text">The text to copy.</param>
    /// <returns>True when a command accepted the text.</returns>
    public static bool TryPlatformCopy(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var (fileName, arguments) in Commands())
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (process is null)
                {
                    continue;
                }

                process.StandardInput.Write(text);
                process.StandardInput.Close();
                if (process.WaitForExit(2000) && process.ExitCode == 0)
                {
                    return true;
                }
            }
            catch (Win32Exception)
            {
                // Not installed; try the next one.
            }
            catch (IOException)
            {
                // The command went away while being written to; try the next one.
            }
        }

        return false;
    }

    private static IEnumerable<(string FileName, string Arguments)> Commands()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return ("pbcopy", "");
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return ("clip.exe", "");
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            yield return ("wl-copy", "");
            yield return ("xclip", "-selection clipboard");
            yield return ("xsel", "--clipboard --input");
        }
    }
}
