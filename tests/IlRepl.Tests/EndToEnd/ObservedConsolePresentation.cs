using Hex1b;
using IlRepl.Tui;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Records completed real console reads so a parent can deliver protocol fragments in independently acknowledged reads.
/// </summary>
internal sealed class ObservedConsolePresentation : IHex1bTerminalPresentationAdapter
{
    private readonly ConsolePresentation _inner = new();
    private int _sequence;

    /// <summary>
    /// Records native chunks before reply framing while retaining the actual console reader and lifecycle.
    /// </summary>
    /// <param name="directory">The parent-owned directory for atomic read acknowledgments.</param>
    internal ObservedConsolePresentation(string directory)
    {
        _inner.InputObserved = bytes =>
        {
            var path = Path.Join(directory, Interlocked.Increment(ref _sequence) + ".read");
            File.WriteAllBytes(path + ".pending", bytes.ToArray());
            File.Move(path + ".pending", path);
        };
    }

    /// <inheritdoc />
    public int Width => _inner.Width;

    /// <inheritdoc />
    public int Height => _inner.Height;

    /// <inheritdoc />
    public TerminalCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public bool AnswersProtocolQueriesDirectly => _inner.AnswersProtocolQueriesDirectly;

    /// <inheritdoc />
    public event Action<int, int>? Resized
    {
        add => _inner.Resized += value;
        remove => _inner.Resized -= value;
    }

    /// <inheritdoc />
    public event Action? Disconnected
    {
        add => _inner.Disconnected += value;
        remove => _inner.Disconnected -= value;
    }

    /// <inheritdoc />
    public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => _inner.WriteOutputAsync(data, ct);

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default) => _inner.ReadInputAsync(ct);

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct = default) => _inner.FlushAsync(ct);

    /// <inheritdoc />
    public ValueTask EnterRawModeAsync(CancellationToken ct = default) => _inner.EnterRawModeAsync(ct);

    /// <inheritdoc />
    public ValueTask ExitRawModeAsync(CancellationToken ct = default) => _inner.ExitRawModeAsync(ct);

    /// <inheritdoc />
    public (int Row, int Column) GetCursorPosition() => _inner.GetCursorPosition();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
