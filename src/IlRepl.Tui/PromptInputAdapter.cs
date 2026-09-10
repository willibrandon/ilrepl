using System.Threading.Channels;
using Hex1b;
using Hex1b.Input;

namespace IlRepl.Tui;

/// <summary>
/// Applies prompt input ordering and key decoding while forwarding the terminal's output unchanged.
/// </summary>
internal sealed class PromptInputAdapter(IHex1bAppTerminalWorkloadAdapter inner, PromptInputReader input)
    : IHex1bAppTerminalWorkloadAdapter
{
    /// <inheritdoc />
    public ChannelReader<Hex1bEvent> InputEvents => input;

    /// <inheritdoc />
    public int Width => inner.Width;

    /// <inheritdoc />
    public int Height => inner.Height;

    /// <inheritdoc />
    public TerminalCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public int OutputQueueDepth => inner.OutputQueueDepth;

    /// <inheritdoc />
    public event Action? Disconnected
    {
        add => inner.Disconnected += value;
        remove => inner.Disconnected -= value;
    }

    /// <inheritdoc />
    public void Write(string text) => inner.Write(text);

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> data) => inner.Write(data);

    /// <inheritdoc />
    public void Write(ReadOnlyMemory<byte> data) => inner.Write(data);

    /// <inheritdoc />
    public void Flush() => inner.Flush();

    /// <inheritdoc />
    public void EnterTuiMode() => inner.EnterTuiMode();

    /// <inheritdoc />
    public void ExitTuiMode() => inner.ExitTuiMode();

    /// <inheritdoc />
    public void Clear() => inner.Clear();

    /// <inheritdoc />
    public void SetCursorPosition(int left, int top) => inner.SetCursorPosition(left, top);

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default) => inner.ReadOutputAsync(ct);

    /// <inheritdoc />
    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => inner.WriteInputAsync(data, ct);

    /// <inheritdoc />
    public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => inner.ResizeAsync(width, height, ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        input.Stop();
        return inner.DisposeAsync();
    }
}
