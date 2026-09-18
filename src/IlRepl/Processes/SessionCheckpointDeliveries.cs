using IlRepl.Protocol;

namespace IlRepl.Processes;

/// <summary>
/// Retains acknowledged workspace documents only for their explicitly registered pending requests.
/// </summary>
internal sealed class SessionCheckpointDeliveries
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionDocument?> _documents = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers one request before its host operation can publish the matching workspace.
    /// </summary>
    /// <returns>The unique identity carried by its checkpoints and final reply.</returns>
    internal string Register()
    {
        var identity = Guid.NewGuid().ToString("N");
        lock (_gate) _documents.Add(identity, null);
        return identity;
    }

    /// <summary>
    /// Retains the complete acknowledged document when its request is still pending.
    /// </summary>
    /// <param name="checkpoint">The expanded checkpoint received before acknowledging the host.</param>
    internal void Remember(SessionReply checkpoint)
    {
        if (checkpoint.CheckpointDelivery is not { } identity) return;
        lock (_gate)
        {
            if (_documents.ContainsKey(identity)) _documents[identity] = checkpoint.Document;
        }
    }

    /// <summary>
    /// Restores a referenced document before the caller releases ownership of its mutation.
    /// </summary>
    /// <param name="reply">The host's final operation reply.</param>
    /// <param name="identity">The identity registered for this operation.</param>
    /// <returns>The complete public reply with transport correlation removed.</returns>
    internal SessionReply Resolve(SessionReply reply, string identity)
    {
        if (reply.CheckpointDelivery is null) return reply;
        lock (_gate)
        {
            if (reply.CheckpointDelivery != identity || !_documents.TryGetValue(identity, out var document) || document is null)
                throw new HostProtocolException("the host returned an unacknowledged workspace document");
            return reply with { Document = document, CheckpointDelivery = null };
        }
    }

    /// <summary>
    /// Releases a request on success, cancellation, or connection loss and ignores subsequent late deliveries.
    /// </summary>
    /// <param name="identity">The completed request's registered identity.</param>
    internal void Forget(string identity)
    {
        lock (_gate) _documents.Remove(identity);
    }
}
