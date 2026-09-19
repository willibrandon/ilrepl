using System.Diagnostics;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Tokens;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Records each completed synchronized terminal frame for behavioral assertions and latency measurements.
/// </summary>
internal sealed class FrameRecorder : IHex1bTerminalPresentationFilter
{
    private const string FrameEnd = "\x1b[?2026l";
    private static readonly Hex1b.Theming.Hex1bColor s_caret = SpanPalette.Color(SpanStyle.Prompt);
    private readonly List<Frame> _frames = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Publishes frames immediately after the terminal has applied the complete synchronized update.
    /// </summary>
    public event Action<Frame>? FrameAdded;

    /// <summary>
    /// The terminal to snapshot; set after the terminal is built.
    /// </summary>
    public Hex1bTerminal? Terminal { get; set; }

    /// <summary>
    /// How many frames have been recorded.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>
    /// Every frame recorded so far.
    /// </summary>
    public IReadOnlyList<Frame> Frames
    {
        get
        {
            lock (_lock)
            {
                return _frames.ToArray();
            }
        }
    }

    /// <summary>
    /// The frames from an index on.
    /// </summary>
    /// <param name="index">The first frame to include.</param>
    /// <returns>The frames.</returns>
    public IReadOnlyList<Frame> Since(int index)
    {
        lock (_lock)
        {
            var first = Math.Clamp(index, 0, _frames.Count);
            return _frames.GetRange(first, _frames.Count - first).ToArray();
        }
    }

    /// <summary>
    /// Finds the caret cell in a snapshot: the cell whose background is the prompt colour.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The cell, or null.</returns>
    public static (int X, int Y)? FindCaret(Hex1bTerminalSnapshot snapshot)
    {
        for (var y = 0; y < snapshot.Height; y++)
        {
            for (var x = 0; x < snapshot.Width; x++)
            {
                if (Equals(snapshot.GetCell(x, y).Background, s_caret))
                {
                    return (x, y);
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(
        IReadOnlyList<AppliedToken> appliedTokens,
        TimeSpan elapsed,
        CancellationToken ct = default)
    {
        var painted = Stopwatch.GetTimestamp();
        var tokens = appliedTokens.Select(a => a.Token).ToList();
        if (Terminal is { } terminal && AnsiTokenSerializer.Serialize(tokens).Contains(FrameEnd, StringComparison.Ordinal))
        {
            using var snapshot = terminal.CreateSnapshot();
            var lines = new string[snapshot.Height];
            for (var y = 0; y < snapshot.Height; y++)
            {
                lines[y] = snapshot.GetLine(y);
            }

            Frame frame;
            lock (_lock)
            {
                frame = new Frame(_frames.Count, lines, FindCaret(snapshot), snapshot.Width, snapshot.Height)
                {
                    Timestamp = painted,
                };
                _frames.Add(frame);
            }

            FrameAdded?.Invoke(frame);
        }

        return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(tokens);
    }

    /// <inheritdoc />
    public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
}
