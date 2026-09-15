using System.Threading.Channels;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Supplies a real pooled channel operation whose value task can be consumed only once.
/// </summary>
public static class ComparisonAsyncSource
{
    /// <summary>
    /// Completes a pending channel read with a value or a real asynchronous failure.
    /// </summary>
    /// <param name="fail">Whether the channel completes with an exception instead of a value.</param>
    /// <returns>The original pooled channel read operation.</returns>
    public static ValueTask<int> Start(bool fail)
    {
        var channel = Channel.CreateBounded<int>(1);
        var pending = channel.Reader.ReadAsync();
        if (fail)
        {
            channel.Writer.TryComplete(new InvalidOperationException("channel failed"));
        }
        else
        {
            channel.Writer.TryWrite(42);
        }

        return pending;
    }
}
