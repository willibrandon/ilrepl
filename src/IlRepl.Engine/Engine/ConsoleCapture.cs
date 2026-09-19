namespace IlRepl.Engine;

/// <summary>
/// Captures console output for one logical execution and supplies EOF on standard input.
/// </summary>
public sealed class ConsoleCapture : IDisposable
{
    private static readonly AsyncLocal<ConsoleCapture?> Current = new();
    private static readonly Lock InstallLock = new();
    private static bool s_installed;
    private readonly CapturedTextWriter _out;
    private readonly CapturedTextWriter _error;
    private readonly ConsoleCapture? _previous;
    private bool _disposed;

    /// <summary>
    /// Starts a capture whose optional observer receives bounded output chunks as they are written.
    /// </summary>
    /// <param name="output">The observer, or null to retain ordinary in-process capture behavior.</param>
    public ConsoleCapture(Action<string, bool>? output = null)
    {
        _out = new CapturedTextWriter(false, output);
        _error = new CapturedTextWriter(true, output);
        EnsureInstalled();
        _previous = Current.Value;
        Current.Value = this;
    }

    /// <summary>
    /// The text written to standard output, bounded when a streaming observer is present.
    /// </summary>
    public string StandardOutput => _out.ToString();

    /// <summary>
    /// The text written to standard error, bounded when a streaming observer is present.
    /// </summary>
    public string StandardError => _error.ToString();

    /// <summary>
    /// Whether the current logical execution owns standard input.
    /// </summary>
    internal static bool IsCapturing => Current.Value is { _disposed: false };

    /// <summary>
    /// Selects an execution's console writer without changing another execution's routing.
    /// </summary>
    internal static TextWriter? Writer(bool error) => Current.Value is { _disposed: false } capture
        ? error ? capture._error : capture._out : null;

    /// <summary>
    /// Restores the prior capture after the current logical execution finishes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Current.Value = _previous;
        _out.Dispose();
        _error.Dispose();
    }

    private static void EnsureInstalled()
    {
        lock (InstallLock)
        {
            if (s_installed)
            {
                return;
            }

            Console.SetOut(new ConsoleRoutingWriter(Console.Out, false));
            Console.SetError(new ConsoleRoutingWriter(Console.Error, true));
            try
            {
                Console.SetIn(new ConsoleRoutingReader(Console.In));
            }
            catch (PlatformNotSupportedException)
            {
            }

            s_installed = true;
        }
    }
}
