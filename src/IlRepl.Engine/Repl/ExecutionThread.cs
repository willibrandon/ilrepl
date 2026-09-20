using System.Collections.Concurrent;
using System.Globalization;

namespace IlRepl.Repl;

/// <summary>
/// Executes synchronous submissions on one thread without replacing its execution context between cells.
/// </summary>
internal sealed class ExecutionThread : IAsyncDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    /// <summary>
    /// Creates the execution thread with the same explicit stack reservation on every supported platform.
    /// </summary>
    internal ExecutionThread()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        _thread = new Thread(() => Run(culture, uiCulture), 8 * 1024 * 1024) { IsBackground = true };
        if (ExecutionContext.IsFlowSuppressed())
        {
            _thread.Start();
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                _thread.Start();
            }
        }
    }

    /// <summary>
    /// Enqueues a synchronous operation without importing the caller's culture or ambient execution context.
    /// </summary>
    internal Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                // A failure that can no longer be handed to the caller must not disappear instead.
                if (!completion.TrySetException(exception))
                {
                    throw;
                }
            }
        }, cancellationToken);

        return completion.Task;
    }

    private void Run(CultureInfo culture, CultureInfo uiCulture)
    {
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = uiCulture;
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                action();
            }
        }
        finally
        {
            _stopped.TrySetResult();
        }
    }

    /// <summary>
    /// Stops accepting operations and waits until the execution thread has drained its work.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _queue.CompleteAdding();
        await _stopped.Task.ConfigureAwait(false);
        _queue.Dispose();
    }
}
