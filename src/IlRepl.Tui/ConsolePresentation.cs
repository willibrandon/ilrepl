using Hex1b;
using Hex1b.Reflow;
using Hex1b.Sixel;

namespace IlRepl.Tui;

/// <summary>
/// Bounds optional console capability discovery before making the focused text editor available for input.
/// </summary>
internal sealed class ConsolePresentation : IHex1bTerminalPresentationAdapter, ITerminalReflowProvider
{
    private readonly ConsolePresentationAdapter _inner =
        new ConsolePresentationAdapter(enableMouse: true).WithSixelSupport(SixelPresentationSupport.None);

    /// <inheritdoc />
    public int Width => _inner.Width;

    /// <inheritdoc />
    public int Height => _inner.Height;

    /// <inheritdoc />
    public TerminalCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public bool AnswersProtocolQueriesDirectly => _inner.AnswersProtocolQueriesDirectly;

    /// <inheritdoc />
    public bool ReflowEnabled => _inner.ReflowEnabled;

    /// <inheritdoc />
    public bool ShouldClearSoftWrapOnAbsolutePosition => _inner.ShouldClearSoftWrapOnAbsolutePosition;

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
    public async ValueTask EnterRawModeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(TimeSpan.FromMilliseconds(25));
        try
        {
            // Hex1b enters raw mode synchronously before optional graphics/background discovery. Its cancellation path
            // settles the reader and retains prefetched input, so the normal input pump never races a background probe.
            await _inner.EnterRawModeAsync(probe.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (probe.IsCancellationRequested && !ct.IsCancellationRequested)
        {
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public ValueTask ExitRawModeAsync(CancellationToken ct = default) => _inner.ExitRawModeAsync(ct);

    /// <inheritdoc />
    public (int Row, int Column) GetCursorPosition() => _inner.GetCursorPosition();

    /// <inheritdoc />
    public ReflowResult Reflow(ReflowContext context) => _inner.Reflow(context);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
