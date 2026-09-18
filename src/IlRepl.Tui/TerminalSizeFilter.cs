using Hex1b;
using Hex1b.Tokens;

namespace IlRepl.Tui;

/// <summary>
/// Tracks terminal dimensions and observes rendered interruption notices before removing their private acknowledgement markers.
/// </summary>
public sealed class TerminalSizeFilter : IHex1bTerminalPresentationFilter
{
    private bool _frameStarted;
    private bool _firstFrameRendered;

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

    /// <summary>
    /// Observes the tokens that reached the terminal presentation pipeline after rendering.
    /// </summary>
    internal event Action<IReadOnlyList<AppliedToken>>? OutputObserved;

    /// <summary>
    /// Announces the first complete synchronized frame after the terminal has applied its rendered cells.
    /// </summary>
    internal event Action? FirstFrameRendered;

    /// <summary>
    /// Delivers an already-disambiguated Escape in the terminal's original input order.
    /// </summary>
    internal event Action? EscapePressed;

    /// <inheritdoc />
    public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        Set(width, height);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(
        IReadOnlyList<AppliedToken> appliedTokens, TimeSpan elapsed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(appliedTokens);
        OutputObserved?.Invoke(appliedTokens);
        if (!_firstFrameRendered)
        {
            foreach (var applied in appliedTokens)
            {
                if (applied.Token is not PrivateModeToken { Mode: 2026 } frame) continue;
                if (frame.Enable) _frameStarted = true;
                else if (_frameStarted)
                {
                    _firstFrameRendered = true;
                    FirstFrameRendered?.Invoke();
                    break;
                }
            }
        }
        return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(appliedTokens.Select(t => t.Token)
            .Where(token => token is not OscToken { Command: "7777" } marker
                || !marker.Payload.StartsWith("ilrepl-interrupt:", StringComparison.Ordinal)).ToList());
    }

    /// <inheritdoc />
    public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
    {
        // The console adapter returns this marker as its own read after joining the Escape ambiguity read.
        // Decline mixed token batches, where injection here could overtake preceding ordinary input.
        if (tokens is [OscToken { Command: "7777", Payload: "ilrepl-escape" }]) EscapePressed?.Invoke();
        return ValueTask.CompletedTask;
    }

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
