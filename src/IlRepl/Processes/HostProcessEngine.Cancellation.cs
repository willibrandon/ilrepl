using IlRepl.Protocol;
using StreamJsonRpc;

namespace IlRepl.Processes;

/// <summary>
/// Correlates caller cancellation with one serialized mutation while retaining its actual remote terminal result.
/// </summary>
public sealed partial class HostProcessEngine
{
    private readonly SemaphoreSlim _mutations = new(1);

    private async Task<HandleReply> InvokeInspectionAsync(Func<CancellationToken, Task<HandleReply>> invoke,
        CancellationToken cancellationToken)
    {
        await _lifetime.WaitForDispatchAsync(cancellationToken).ConfigureAwait(false);
        // Prepared inspections own isolated workers, so reset and editing can invalidate them while they execute.
        // Their RPC cancellation terminates those workers without interrupting a concurrent host mutation.
        return await invoke(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> InvokeMutationAsync<T>(Func<CancellationToken, Task<T>> invoke, CancellationToken cancellationToken)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifetime.WaitForDispatchAsync(cancellationToken).ConfigureAwait(false);
            var baseline = Progress.Sequence;
            var requested = 0;
            void Observe(ExecutionProgress progress)
            {
                if (cancellationToken.IsCancellationRequested && progress.Sequence > baseline && progress.IsRunning
                    && Interlocked.Exchange(ref requested, 1) == 0)
                    _ = SendCancellationAsync(progress.Identity);
            }
            ProgressChanged += Observe;
            try
            {
                using var registration = cancellationToken.Register(() => Observe(Progress));
                cancellationToken.ThrowIfCancellationRequested();
                return await invoke(CancellationToken.None).ConfigureAwait(false);
            }
            finally { ProgressChanged -= Observe; }
        }
        finally { _mutations.Release(); }
    }

    private async Task SendCancellationAsync(string identity)
    {
        try { await _host.InterruptAsync(identity, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ConnectionLostException or RemoteInvocationException or ObjectDisposedException)
        {
            // The original invocation still observes process loss and publishes the terminal result.
        }
    }
}
