using System.Text;
using System.Threading.Channels;
using Hex1b;
using Hex1b.Input;
using Hex1b.Tokens;

namespace IlRepl.Tui;

/// <summary>
/// Applies prompt input ordering and key decoding while forwarding the terminal's output unchanged.
/// </summary>
internal sealed class PromptInputAdapter(
    IHex1bAppTerminalWorkloadAdapter inner,
    PromptInputReader input,
    Func<string?>? frameMarker = null)
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
    public void Write(string text)
    {
        inner.Write(text);
        if (ContainsInterruptNotice(text))
        {
            WriteFrameMarker();
        }
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> data)
    {
        inner.Write(data);
        if (data.IndexOf("Press"u8) >= 0 && ContainsInterruptNotice(Encoding.UTF8.GetString(data)))
        {
            WriteFrameMarker();
        }
    }

    /// <inheritdoc />
    public void Write(ReadOnlyMemory<byte> data) => Write(data.Span);

    private void WriteFrameMarker()
    {
        if (frameMarker?.Invoke() is { } marker)
        {
            inner.Write(marker);
        }
    }

    private static bool ContainsInterruptNotice(string text)
    {
        if (text.Contains("Press Ctrl+C again", StringComparison.Ordinal))
        {
            return true;
        }

        if (!text.Contains("Press", StringComparison.Ordinal))
        {
            return false;
        }

        // Narrow terminals wrap the notice between ANSI cursor moves and may omit the inter-word spaces.
        var painted = string.Concat(AnsiTokenizer.Tokenize(text).OfType<TextToken>().Select(token => token.Text));
        return painted.Replace(" ", "", StringComparison.Ordinal).Contains("PressCtrl+Cagain", StringComparison.Ordinal);
    }

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
