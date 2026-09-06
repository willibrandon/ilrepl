using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using Hex1b;

namespace IlRepl.Wasm;

/// <summary>
/// Bridges a Hex1b terminal to xterm.js through a Web Worker. Output bytes are posted to the
/// main thread; input and resizes arrive through JavaScript queues that JavaScript signals into
/// <see cref="ReadInputAsync"/>. This follows the pattern of Hex1b's own WasmDemo sample.
/// </summary>
public sealed partial class WasmPresentationAdapter : IHex1bTerminalPresentationAdapter
{
    private static TaskCompletionSource? s_inputSignal;
    private int _width;
    private int _height;

    /// <summary>
    /// Initializes the adapter with the terminal's starting size.
    /// </summary>
    /// <param name="initialColumns">The initial column count.</param>
    /// <param name="initialRows">The initial row count.</param>
    public WasmPresentationAdapter(int initialColumns, int initialRows)
    {
        _width = initialColumns;
        _height = initialRows;
    }

    /// <summary>
    /// The adapter in use, so exported functions can reach it.
    /// </summary>
    public static WasmPresentationAdapter? Instance { get; set; }

    /// <inheritdoc />
    public int Width => _width;

    /// <inheritdoc />
    public int Height => _height;

    /// <inheritdoc />
    public TerminalCapabilities Capabilities => new()
    {
        SupportsTrueColor = true,
        Supports256Colors = true,
        SupportsAlternateScreen = true,
        SupportsBracketedPaste = true,
        SupportsSixel = false,
        SupportsMouse = true,
    };

    /// <inheritdoc />
    public event Action<int, int>? Resized;

    /// <inheritdoc />
    public event Action? Disconnected;

    /// <inheritdoc />
    public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        PostOutput(data.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ApplyPendingResize();

            var input = PollAllInput();
            if (input is { Length: > 0 })
            {
                return new ReadOnlyMemory<byte>(input);
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref s_inputSignal, signal);

            var lateInput = PollAllInput();
            if (lateInput is { Length: > 0 })
            {
                Interlocked.CompareExchange(ref s_inputSignal, null, signal);
                return new ReadOnlyMemory<byte>(lateInput);
            }

            try
            {
                using var registration = ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), signal);
                // The signal normally wins; the delay is a fallback in case the JavaScript export was not wired.
                await Task.WhenAny(signal.Task, Task.Delay(50, ct)).ConfigureAwait(false);
                Interlocked.CompareExchange(ref s_inputSignal, null, signal);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask EnterRawModeAsync(CancellationToken ct) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask ExitRawModeAsync(CancellationToken ct) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public (int Row, int Column) GetCursorPosition() => (0, 0);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Disconnected?.Invoke();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reads and clears the pending resize as <c>cols,rows</c>, or an empty string.
    /// </summary>
    /// <returns>The pending resize.</returns>
    [JSImport("pollResize", "main.js")]
    internal static partial string PollResize();

    /// <summary>
    /// Tells the page that the app is rendering.
    /// </summary>
    /// <param name="cols">The column count.</param>
    /// <param name="rows">The row count.</param>
    [JSImport("notifyReady", "main.js")]
    internal static partial void NotifyReady(int cols, int rows);

    /// <summary>
    /// Tells the page that the session ended and the next one is about to start.
    /// </summary>
    [JSImport("notifyExited", "main.js")]
    internal static partial void NotifyExited();

    /// <summary>
    /// Wakes <see cref="ReadInputAsync"/>. JavaScript calls this when input or a resize arrives.
    /// </summary>
    [JSExport]
    internal static void SignalInputAvailable()
    {
        var signal = Interlocked.Exchange(ref s_inputSignal, null);
        signal?.TrySetResult();
    }

    [JSImport("postTerminalOutput", "main.js")]
    private static partial void PostOutput(byte[] data);

    [JSImport("pollAllInput", "main.js")]
    private static partial byte[]? PollAllInput();

    private void ApplyPendingResize()
    {
        var resize = PollResize();
        if (string.IsNullOrEmpty(resize))
        {
            return;
        }

        var parts = resize.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cols)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows))
        {
            return;
        }

        if (cols == _width && rows == _height)
        {
            return;
        }

        _width = cols;
        _height = rows;
        // Raise on the next turn of the event loop rather than from inside the read.
        _ = Task.Run(() => Resized?.Invoke(cols, rows));
    }
}
