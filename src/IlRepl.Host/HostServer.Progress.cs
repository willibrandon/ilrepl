using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Carries source completion with its reply while acknowledging execution boundaries and exceptional completion independently.
/// </summary>
public sealed partial class HostServer
{
    private readonly AsyncLocal<HostCompletionDelivery?> _completionDelivery = new();

    private async Task<HandleReply> HandleWithCompletionAsync(Func<Task<HandleReply>> action)
    {
        var previous = _completionDelivery.Value;
        var delivery = new HostCompletionDelivery();
        _completionDelivery.Value = delivery;
        try
        {
            var reply = await action().ConfigureAwait(false);
            return WithStreamedOutput(reply) with { CompletionProgress = delivery.Pending };
        }
        catch
        {
            // Exceptional RPC replies cannot carry HandleReply, so retain the usual acknowledged terminal notification.
            await FlushCompletionAsync(delivery).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _completionDelivery.Value = previous;
        }
    }

    private async Task PublishProgressAsync(ExecutionProgress progress)
    {
        if (_completionDelivery.Value is { } delivery)
        {
            // One source command can start another operation, such as loading a dependency after parsing its request.
            await FlushCompletionAsync(delivery).ConfigureAwait(false);
            if (!progress.IsRunning)
            {
                delivery.Pending = progress;
                return;
            }
        }

        if (_client is { } client)
            await client.ExecutionChangedAsync(progress, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FlushCompletionAsync(HostCompletionDelivery delivery)
    {
        if (delivery.Pending is not { } progress) return;
        delivery.Pending = null;
        if (_client is { } client)
            await client.ExecutionChangedAsync(progress, CancellationToken.None).ConfigureAwait(false);
    }
}
