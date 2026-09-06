using Hex1b;
using Hex1b.Tokens;

namespace IlRepl.Tui;

/// <summary>
/// A presentation filter that turns the prompt's caret into a blinking block. The text box asks
/// the terminal for a bar caret, which is easy to lose against the prompt band, so the bar shapes
/// are rewritten on the way out. Everything else passes through unchanged.
/// </summary>
public sealed class PromptCursorFilter : IHex1bTerminalPresentationFilter
{
    /// <inheritdoc />
    public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(IReadOnlyList<AppliedToken> appliedTokens, TimeSpan elapsed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(appliedTokens);
        return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(appliedTokens.Select(t => Rewrite(t.Token)).ToList());
    }

    /// <inheritdoc />
    public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <summary>
    /// Maps a bar caret to a blinking block and leaves any other token alone. The terminal hands
    /// the caret sequence over as an unrecognized sequence, so both forms are handled.
    /// </summary>
    /// <param name="token">The token on its way to the terminal.</param>
    /// <returns>The token to send.</returns>
    public static AnsiToken Rewrite(AnsiToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token switch
        {
            CursorShapeToken { Shape: 5 or 6 } => CursorShapeToken.BlinkingBlock,
            UnrecognizedSequenceToken { Sequence: "\x1b[5 q" or "\x1b[6 q" } => CursorShapeToken.BlinkingBlock,
            _ => token,
        };
    }
}
