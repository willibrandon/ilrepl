using System.Text;
using Hex1b;
using Hex1b.Tokens;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// A workload filter that keeps what a process wrote to the terminal, so a test can look for
/// sequences the emulator does not surface, such as the caret shape.
/// </summary>
internal sealed class WorkloadRecorder : IHex1bTerminalWorkloadFilter
{
    private readonly StringBuilder _output = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Everything written so far, serialized.
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

    /// <inheritdoc />
    public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnOutputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
    {
        var text = AnsiTokenSerializer.Serialize(tokens);
        lock (_lock)
        {
            _output.Append(text);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnFrameCompleteAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
}
