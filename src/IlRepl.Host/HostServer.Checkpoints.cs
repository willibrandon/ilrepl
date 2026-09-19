using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Publishes accepted source and acknowledges the final execution boundary before arbitrary user IL can run.
/// </summary>
public sealed partial class HostServer
{
    private readonly SessionCheckpointStore _checkpoints = new();
    private SessionDocument? _lastCheckpoint;
    private string? _checkpointPath;
    private bool _checkpointDirty;

    private void PublishCheckpoint(bool executing)
    {
        if (_client is not { } client)
        {
            return;
        }

        var document = _core.CaptureSession(new SessionEditor());
        if (_lastCheckpoint is { } previous)
        {
            _checkpointDirty |= !previous.Entries.SequenceEqual(document.Entries)
                || !previous.Cells.SequenceEqual(document.Cells) || !previous.References.SequenceEqual(document.References);
        }
        else
        {
            _checkpointDirty = document.Entries.Length != 0;
        }

        var checkpoint = new SessionReply
        {
            Document = document, Path = _checkpointPath, Dirty = _checkpointDirty,
            PendingSubmission = executing ? _core.CellNumber : null,
            PendingSource = executing ? [.. _core.Session.BodyLines, "ret"] : [],
            PendingInputs = executing ? _core.PendingInputDeclarations : [],
            Reply = new HandleReply(true, false, [], _core.Status),
        };
        _lastCheckpoint = document;
        client.CheckpointAsync(_checkpoints.Encode(checkpoint), CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task PublishWorkspaceAsync(SessionReply workspace, CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            return;
        }

        _checkpointPath = workspace.Path;
        _checkpointDirty = workspace.Dirty;
        _lastCheckpoint = workspace.Document;
        var checkpoint = workspace with { Reply = workspace.Reply with { Lines = [] } };
        await client.CheckpointAsync(_checkpoints.Encode(checkpoint), cancellationToken).ConfigureAwait(false);
    }
}
