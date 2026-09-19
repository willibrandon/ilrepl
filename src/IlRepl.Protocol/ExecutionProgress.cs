namespace IlRepl.Protocol;

/// <summary>
/// Describes a sequenced execution phase without requiring the engine mutation gate.
/// </summary>
/// <param name="Identity">The operation identity, or an empty string before the first operation.</param>
/// <param name="Sequence">The monotonically increasing phase revision.</param>
/// <param name="Phase">The current cancellation behavior.</param>
/// <param name="IsRunning">Whether the operation has not yet settled.</param>
/// <param name="CancellationRequested">Whether interruption has been requested.</param>
/// <param name="Name">The operation name displayed while cancellation settles.</param>
public sealed record ExecutionProgress(
    string Identity,
    long Sequence,
    ExecutionPhase Phase,
    bool IsRunning,
    bool CancellationRequested = false,
    string Name = "operation");
