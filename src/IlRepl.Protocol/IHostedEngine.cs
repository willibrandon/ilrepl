namespace IlRepl.Protocol;

/// <summary>
/// Exposes recoverable process lifetime and source acknowledgements to a frontend controller.
/// </summary>
public interface IHostedEngine : IInterruptibleEngine
{
    /// <summary>
    /// Handles source retained by the frontend until the next checkpoint without acknowledging a redundant intermediate revision.
    /// </summary>
    /// <param name="line">The retained instruction inside an open method.</param>
    /// <param name="location">Its location in the submitting document.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The accepted source and current execution status.</returns>
    Task<HandleReply> HandleRetainedSourceAsync(string line, AnalysisLocation location, CancellationToken cancellationToken);

    /// <summary>
    /// Handles a run of frontend-retained instructions in one operation, ending at the first line that is not plainly accepted.
    /// </summary>
    /// <param name="lines">The retained instructions inside an open method, in order.</param>
    /// <param name="locations">Their locations in the submitting document.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One reply for each line handled, which can be fewer than were sent.</returns>
    Task<HandleReply[]> HandleRetainedSourceRunAsync(string[] lines, AnalysisLocation[] locations, CancellationToken cancellationToken);

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
