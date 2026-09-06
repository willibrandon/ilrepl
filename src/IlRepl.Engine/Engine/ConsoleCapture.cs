namespace IlRepl.Engine;

/// <summary>
/// Captures what a cell writes to the console while it runs. The console is redirected
/// through routers that consult an async-local capture, so cells running on different
/// logical threads capture independently and everything else keeps writing to the real
/// console. A read from <see cref="Console.In"/> inside a capture returns end-of-input instead
/// of blocking.
/// </summary>
public sealed class ConsoleCapture : IDisposable
{
    private static readonly AsyncLocal<ConsoleCapture?> Current = new();
    private static readonly object InstallLock = new();
    private static bool s_installed;

    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();
    private readonly ConsoleCapture? _previous;
    private bool _disposed;

    /// <summary>
    /// Starts capturing on the current logical thread.
    /// </summary>
    public ConsoleCapture()
    {
        EnsureInstalled();
        _previous = Current.Value;
        Current.Value = this;
    }

    /// <summary>
    /// The text written to standard output so far.
    /// </summary>
    public string StandardOutput => _out.ToString();

    /// <summary>
    /// The text written to standard error so far.
    /// </summary>
    public string StandardError => _error.ToString();

    /// <summary>
    /// Stops capturing.
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

            var originalOut = Console.Out;
            var originalError = Console.Error;
            Console.SetOut(new RoutingWriter(originalOut, capture => capture._out));
            Console.SetError(new RoutingWriter(originalError, capture => capture._error));
            try
            {
                Console.SetIn(new RoutingReader(Console.In));
            }
            catch (PlatformNotSupportedException)
            {
                // The browser has no standard input; reads already return end-of-input there.
            }

            s_installed = true;
        }
    }

    private sealed class RoutingWriter(TextWriter fallback, Func<ConsoleCapture, TextWriter> select) : TextWriter
    {
        private TextWriter Target => Current.Value is { _disposed: false } capture ? select(capture) : fallback;

        public override System.Text.Encoding Encoding => Target.Encoding;

        public override IFormatProvider FormatProvider => Target.FormatProvider;

        public override void Write(char value) => Target.Write(value);

        public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);

        public override void Write(ReadOnlySpan<char> buffer) => Target.Write(buffer);

        public override void Write(string? value) => Target.Write(value);

        public override void WriteLine(string? value) => Target.WriteLine(value);

        public override void Flush() => Target.Flush();
    }

    private sealed class RoutingReader(TextReader fallback) : TextReader
    {
        private TextReader Target => Current.Value is { _disposed: false } ? Null : fallback;

        public override int Peek() => Target.Peek();

        public override int Read() => Target.Read();

        public override int Read(char[] buffer, int index, int count) => Target.Read(buffer, index, count);

        public override string? ReadLine() => Target.ReadLine();

        public override string ReadToEnd() => Target.ReadToEnd();
    }
}
