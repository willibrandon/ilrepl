using IlRepl.Protocol;

namespace IlRepl.Processes;

/// <summary>
/// Registers the frontend callback target before the host handshake creates its engine adapter.
/// </summary>
internal sealed class HostEventReceiver : IReplClient
{
    /// <summary>
    /// The initialized engine that receives events after the host hello.
    /// </summary>
    internal IReplClient? Client { get; set; }

    /// <inheritdoc />
    public Task ExecutionChangedAsync(ExecutionProgress progress, CancellationToken cancellationToken) =>
        Client?.ExecutionChangedAsync(progress, cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc />
    public Task CheckpointAsync(SessionReply checkpoint, CancellationToken cancellationToken) =>
        Client?.CheckpointAsync(checkpoint, cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc />
    public Task OutputAsync(ExecutionOutput output, CancellationToken cancellationToken) =>
        Client?.OutputAsync(output, cancellationToken) ?? Task.CompletedTask;
    /// <inheritdoc />
    public Task RegisterProcessAsync(OwnedProcessScope scope, CancellationToken cancellationToken) =>
        Client?.RegisterProcessAsync(scope, cancellationToken) ?? Task.CompletedTask;
}
