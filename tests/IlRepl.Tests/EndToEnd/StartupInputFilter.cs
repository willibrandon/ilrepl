using Hex1b;
using Hex1b.Tokens;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Sends real input at capability probing or alternate-screen entry before the child's initial editor frame.
/// </summary>
internal sealed class StartupInputFilter(byte[] input, bool atTuiEntry) : IHex1bTerminalPresentationFilter
{
    private bool _sent;

    /// <summary>
    /// The actual pseudoterminal receiving input.
    /// </summary>
    internal Hex1bTerminal Terminal { get; set; } = null!;

    /// <summary>
    /// Completes once the early input has reached the child's input stream.
    /// </summary>
    internal TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(
        IReadOnlyList<AppliedToken> appliedTokens, TimeSpan elapsed, CancellationToken ct = default)
    {
        var tokens = appliedTokens.Select(item => item.Token).ToArray();
        if (!_sent && tokens.Any(token => atTuiEntry ? token is PrivateModeToken { Mode: 1049, Enable: true } : token is KgpToken))
        {
            _sent = true;
            await Terminal.SendInputAsync(input, ct);
            Written.TrySetResult();
        }
        return tokens;
    }

    /// <inheritdoc />
    public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
}
