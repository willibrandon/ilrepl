namespace IlRepl.Protocol;

/// <summary>
/// Exposes process-ownership recovery without replacing the execution runtime.
/// </summary>
public interface IProcessSupervision
{
    /// <summary>
    /// The latest acknowledged ownership state.
    /// </summary>
    ProcessSupervisionState Supervision { get; }

    /// <summary>
    /// Reports adoption progress and failures independently of host execution.
    /// </summary>
    event Action<ProcessSupervisionState>? SupervisionChanged;

    /// <summary>
    /// Retries adoption while preserving the existing runtime and its source.
    /// </summary>
    /// <param name="cancellationToken">Cancels the retry.</param>
    /// <returns>Completion after ownership is restored.</returns>
    Task RetrySupervisionAsync(CancellationToken cancellationToken);
}
