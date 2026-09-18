using PolyType;
using StreamJsonRpc;

namespace IlRepl.Protocol;

/// <summary>
/// Receives execution notifications directly from the host and acknowledges recovery source.
/// </summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IReplClient
{
    /// <summary>
    /// Observes an operation phase or terminal state independently of its invocation reply.
    /// </summary>
    /// <param name="progress">The sequenced phase.</param>
    /// <param name="cancellationToken">Cancels notification delivery.</param>
    /// <returns>Completion after the frontend has observed the phase.</returns>
    Task ExecutionChangedAsync(ExecutionProgress progress, CancellationToken cancellationToken);

    /// <summary>
    /// Retains accepted source before the host enters user execution.
    /// </summary>
    /// <param name="checkpoint">The source and pending execution captured by the host.</param>
    /// <param name="cancellationToken">Cancels checkpoint delivery.</param>
    /// <returns>Completion after the frontend owns the recovery source.</returns>
    Task CheckpointAsync(SessionReply checkpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Retains console output while the operation is still running.
    /// </summary>
    /// <param name="output">The ordered output portion.</param>
    /// <param name="cancellationToken">Cancels output delivery.</param>
    /// <returns>Completion after the frontend has accepted the output.</returns>
    Task OutputAsync(ExecutionOutput output, CancellationToken cancellationToken);
    /// <summary>
    /// Registers a prepared descendant group before it can execute user code.
    /// </summary>
    /// <param name="scope">The stable process identity to adopt.</param>
    /// <param name="cancellationToken">Cancels ownership acknowledgement.</param>
    /// <returns>Completion after the frontend owns the group.</returns>
    Task RegisterProcessAsync(OwnedProcessScope scope, CancellationToken cancellationToken);
}
