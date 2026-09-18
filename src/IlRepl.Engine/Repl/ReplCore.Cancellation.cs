using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Exposes execution boundaries without allowing control messages to mutate the live session.
/// </summary>
public sealed partial class ReplCore
{
    private CancellationToken _cancellationToken;

    /// <summary>
    /// Acknowledges retained source before the core enters user execution.
    /// </summary>
    public Action? BeforeExecution { get; set; }

    /// <summary>
    /// Retains the accepted source revision after one input line has settled.
    /// </summary>
    public Action? SourceCheckpoint { get; set; }

    /// <summary>
    /// Reports transitions between cooperative engine work and arbitrary user code.
    /// </summary>
    public Action<ExecutionPhase>? PhaseChanged { get; set; }

    /// <summary>
    /// Streams bounded user console chunks without waiting for the cell to return.
    /// </summary>
    public Action<string, bool>? OutputReceived { get; set; }
}
