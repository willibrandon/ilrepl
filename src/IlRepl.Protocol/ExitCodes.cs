using System.Globalization;

namespace IlRepl.Protocol;

/// <summary>
/// Writes a process exit code the way its platform's developers read it, with the name of a fatal status or signal.
/// </summary>
public static class ExitCodes
{
    /// <summary>
    /// Describes an exit code of a process on this machine.
    /// </summary>
    /// <remarks>
    /// The number can be all a process leaves. On Windows, a garbage collection that suspends a thread as its stack runs out
    /// ends the runtime with an access violation before it writes anything, and -1073741819 alone says little.
    /// </remarks>
    /// <param name="code">The observed exit code.</param>
    /// <returns>The code, and the name of the failure it stands for when that is known.</returns>
    public static string Describe(int code) => Describe(code, OperatingSystem.IsWindows());

    /// <summary>
    /// Describes an exit code of a Windows process or of a Unix process.
    /// </summary>
    /// <param name="code">The observed exit code.</param>
    /// <param name="windows">Whether the process ran on Windows.</param>
    /// <returns>The code, and the name of the failure it stands for when that is known.</returns>
    public static string Describe(int code, bool windows)
    {
        if (windows)
        {
            // A failed NTSTATUS or HRESULT has its top bit set, and Windows developers know those values in hexadecimal.
            if (code >= 0)
            {
                return code.ToString(CultureInfo.InvariantCulture);
            }

            var status = "0x" + unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture);
            return Status(unchecked((uint)code)) is { } name ? status + " (" + name + ")" : status;
        }

        // A process ended by a signal reports 128 plus the signal's number.
        var text = code.ToString(CultureInfo.InvariantCulture);
        return Signal(code - 128) is { } signal ? text + " (" + signal + ")" : text;
    }

    private static string? Status(uint status) => status switch
    {
        0xC00000FD or 0x800703E9 => "stack overflow",
        0xC0000005 => "access violation",
        0xC0000409 or 0x80131623 => "fail fast",
        0xC000013A => "Ctrl+C",
        0xC0000017 => "out of memory",
        0xE0434352 => "unhandled .NET exception",
        0x80131506 => "internal runtime error",
        _ => null,
    };

    // Only the numbers that Linux and macOS share.
    private static string? Signal(int signal) => signal switch
    {
        1 => "SIGHUP",
        2 => "SIGINT",
        3 => "SIGQUIT",
        4 => "SIGILL",
        5 => "SIGTRAP",
        6 => "SIGABRT",
        8 => "SIGFPE",
        9 => "SIGKILL",
        11 => "SIGSEGV",
        13 => "SIGPIPE",
        14 => "SIGALRM",
        15 => "SIGTERM",
        _ => null,
    };
}
