namespace IlRepl.Batch;

/// <summary>
/// Maps Windows console interruption, including Ctrl+Break, to the batch operation's cancellation token.
/// </summary>
internal sealed class BatchConsoleCancellation : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation;
    private bool _disposed;

    /// <summary>
    /// Keeps Windows console cancellation registered until the batch runtime has finished cleanup.
    /// </summary>
    /// <param name="cancellationToken">The command invocation's existing cancellation token.</param>
    internal BatchConsoleCancellation(CancellationToken cancellationToken)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (OperatingSystem.IsWindows())
        {
            Console.CancelKeyPress += OnCancelKeyPress;
        }
    }

    /// <summary>
    /// Gets cancellation from the command invocation or a Windows console interrupt.
    /// </summary>
    internal CancellationToken Token => _cancellation.Token;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        lock (_sync)
        {
            if (!_disposed)
            {
                _cancellation.Cancel();
            }
        }
    }

    /// <summary>
    /// Removes this operation's console handler and releases its linked cancellation source.
    /// </summary>
    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cancellation.Dispose();
        }
    }
}
