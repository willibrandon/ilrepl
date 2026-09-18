namespace IlRepl.Protocol;

/// <summary>
/// Provides cancellation control independently of the session's execution gate.
/// </summary>
public interface IInterruptibleEngine
{
    /// <summary>
    /// The most recently observed execution phase.
    /// </summary>
    ExecutionProgress Progress { get; }

    /// <summary>
    /// Publishes phase changes and operation settlement.
    /// </summary>
    event Action<ExecutionProgress>? ProgressChanged;

    /// <summary>
    /// Requests interruption of the matching operation without abandoning its terminal reply.
    /// </summary>
    /// <param name="identity">The operation that the user intends to interrupt.</param>
    /// <param name="cancellationToken">Cancels delivery of the control request.</param>
    /// <returns>Whether the operation was still active when the request arrived.</returns>
    Task<bool> InterruptAsync(string identity, CancellationToken cancellationToken);
}
