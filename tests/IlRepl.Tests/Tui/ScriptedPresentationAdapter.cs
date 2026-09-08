using System.Text;
using System.Threading.Channels;
using Hex1b;

namespace IlRepl.Tests.Tui;

/// <summary>
/// A presentation adapter that feeds the terminal whatever bytes a test writes, so a bracketed
/// paste reaches the app the way a real terminal sends one: through the terminal's own input
/// parser, which turns the markers into a paste event. Keys still go through the automator.
/// Output is discarded; snapshots come from the terminal itself.
/// </summary>
internal sealed class ScriptedPresentationAdapter : IHex1bTerminalPresentationAdapter
{
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();

    /// <summary>
    /// Initializes an adapter of a fixed size.
    /// </summary>
    /// <param name="width">The width in columns.</param>
    /// <param name="height">The height in rows.</param>
    public ScriptedPresentationAdapter(int width, int height)
    {
        Width = width;
        Height = height;
    }

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    /// <inheritdoc />
    public TerminalCapabilities Capabilities => new()
    {
        SupportsTrueColor = true,
        Supports256Colors = true,
        SupportsAlternateScreen = true,
        SupportsBracketedPaste = true,
        SupportsMouse = true,
    };

    /// <inheritdoc />
    public event Action<int, int>? Resized;

    /// <inheritdoc />
    public event Action? Disconnected;

    /// <summary>
    /// Sends a bracketed paste: the payload between the paste markers, as a terminal sends it.
    /// </summary>
    /// <param name="text">The payload.</param>
    /// <returns>A task that completes once the bytes are queued.</returns>
    public Task PasteAsync(string text) => _input.Writer.WriteAsync(Encoding.UTF8.GetBytes("\x1b[200~" + text + "\x1b[201~")).AsTask();

    /// <summary>
    /// Sends raw bytes.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>A task that completes once the bytes are queued.</returns>
    public Task SendAsync(byte[] bytes) => _input.Writer.WriteAsync(bytes).AsTask();

    /// <summary>
    /// Raises a resize.
    /// </summary>
    /// <param name="width">The new width.</param>
    /// <param name="height">The new height.</param>
    public void Resize(int width, int height) => Resized?.Invoke(width, height);

    /// <inheritdoc />
    public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default)
    {
        try
        {
            return await _input.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        catch (ChannelClosedException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask EnterRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask ExitRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public (int Row, int Column) GetCursorPosition() => (0, 0);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _input.Writer.TryComplete();
        Disconnected?.Invoke();
        return ValueTask.CompletedTask;
    }
}
