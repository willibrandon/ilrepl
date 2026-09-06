using System.Text;
using Hex1b;
using Hex1b.Tokens;

namespace IlRepl.Tests.Tui;

/// <summary>
/// A presentation filter that keeps what the app sent to the terminal, so tests can look for
/// sequences the emulator does not surface, such as the clipboard write and the caret shape.
/// </summary>
internal sealed class PresentationRecorder : IHex1bTerminalPresentationFilter
{
    private readonly StringBuilder _output = new();
    private readonly List<AnsiToken> _tokens = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Everything sent so far, serialized.
    /// </summary>
    public string Output
    {
        get
        {
            lock (_lock)
            {
                return _output.ToString();
            }
        }
    }

    /// <summary>
    /// Every token sent so far.
    /// </summary>
    public IReadOnlyList<AnsiToken> Tokens
    {
        get
        {
            lock (_lock)
            {
                return _tokens.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(IReadOnlyList<AppliedToken> appliedTokens, TimeSpan elapsed, CancellationToken ct = default)
    {
        var tokens = appliedTokens.Select(t => t.Token).ToList();
        lock (_lock)
        {
            _tokens.AddRange(tokens);
            _output.Append(AnsiTokenSerializer.Serialize(tokens));
        }

        return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(tokens);
    }

    /// <inheritdoc />
    public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
}
