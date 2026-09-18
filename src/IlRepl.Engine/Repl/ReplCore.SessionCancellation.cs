namespace IlRepl.Repl;

/// <summary>
/// Propagates a session operation's cancellation through synchronous source and dependency reconstruction.
/// </summary>
public sealed partial class ReplCore
{
    /// <summary>
    /// Applies the current operation token while preserving the enclosing core operation's cancellation context.
    /// </summary>
    internal T WithCancellation<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var previous = _cancellationToken;
        _cancellationToken = cancellationToken;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }
        finally
        {
            _cancellationToken = previous;
        }
    }
}
