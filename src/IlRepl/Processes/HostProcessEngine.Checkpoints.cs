using IlRepl.Protocol;

namespace IlRepl.Processes;

/// <summary>
/// Reconstructs acknowledged incremental source revisions on the frontend side of the direct connection.
/// </summary>
public sealed partial class HostProcessEngine
{
    private readonly SessionCheckpointStore _checkpoints = new();

    private SessionReply? AcceptCheckpoint(SessionReply checkpoint) => _checkpoints.Apply(checkpoint);
}
