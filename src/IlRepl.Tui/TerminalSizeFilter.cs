using Hex1b;
using Hex1b.Tokens;

namespace IlRepl.Tui;

/// <summary>
/// A presentation filter that only listens: it records the terminal's size at session start and
/// on every resize, so the layout can fold long lines at the width they will be shown in.
/// </summary>
public sealed class TerminalSizeFilter : IHex1bTerminalPresentationFilter
{
    /// <summary>
    /// The current width in columns, or zero before the session has started.
    /// </summary>
    public int Width { get; private set; }

    /// <summary>
    /// The current height in rows, or zero before the session has started.
    /// </summary>
    public int Height { get; private set; }

    /// <summary>
    /// Raised after the size changes.
    /// </summary>
    public event Action? Changed;

    /// <inheritdoc />
    public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        Set(width, height);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(IReadOnlyList<AppliedToken> appliedTokens, TimeSpan elapsed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(appliedTokens);
        return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(appliedTokens.Select(t => t.Token).ToList());
    }

    /// <inheritdoc />
    public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default)
    {
        Set(width, height);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    private void Set(int width, int height)
    {
        if (width == Width && height == Height)
        {
            return;
        }

        Width = width;
        Height = height;
        Changed?.Invoke();
    }
}
