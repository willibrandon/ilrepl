namespace IlRepl.Protocol;

/// <summary>
/// Exposes recoverable process lifetime and source acknowledgements to a frontend controller.
/// </summary>
public interface IHostedEngine : IInterruptibleEngine
{
    /// <summary>
    /// Publishes acknowledged source before execution crosses into user code.
    /// </summary>
    event Action<SessionReply>? CheckpointReceived;

    /// <summary>
    /// Delivers user output before the operation returns.
    /// </summary>
    event Action<ExecutionOutput>? OutputReceived;

    /// <summary>
    /// Reports observed host termination, including intentional shutdown.
    /// </summary>
    event Action<HostExit>? Exited;

    /// <summary>
    /// Terminates the owned runtime after explicit frontend authorization.
    /// </summary>
    /// <param name="cancellationToken">Bounds the wait for actual termination.</param>
    /// <returns>Completion after owned execution has stopped.</returns>
    Task TerminateAsync(CancellationToken cancellationToken);
}
