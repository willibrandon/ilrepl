using IlRepl.Protocol;

namespace IlRepl.Processes;

/// <summary>
/// Reconstructs acknowledged incremental source revisions on the frontend side of the direct connection.
/// </summary>
public sealed partial class HostProcessEngine
{
    private readonly SessionCheckpointStore _checkpoints = new();
    private readonly SessionCheckpointDeliveries _deliveries = new();

    private SessionReply? AcceptCheckpoint(SessionReply checkpoint)
    {
        var accepted = _checkpoints.Apply(checkpoint);
        if (accepted is null)
        {
            return null;
        }

        _deliveries.Remember(accepted);
        return accepted with { CheckpointDelivery = null };
    }
}
