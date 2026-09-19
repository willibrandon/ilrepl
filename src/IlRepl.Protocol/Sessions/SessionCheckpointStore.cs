namespace IlRepl.Protocol;

/// <summary>
/// Encodes or reconstructs ordered checkpoints without retransmitting previously acknowledged images and source.
/// </summary>
public sealed class SessionCheckpointStore
{
    private SessionDocument _previous = new();
    private long _sequence;

    /// <summary>
    /// Encodes a complete source revision as changed tails following the preceding acknowledged revision.
    /// </summary>
    /// <param name="checkpoint">The complete revision retained by the sender.</param>
    /// <returns>The transport envelope containing changed source and new images.</returns>
    public SessionReply Encode(SessionReply checkpoint)
    {
        var document = checkpoint.Document;
        var entriesKept = Prefix(_previous.Entries, document.Entries, SessionCheckpointDelta.EntryEquals);
        var sourceKept = entriesKept < Math.Min(_previous.Entries.Length, document.Entries.Length)
            && _previous.Entries[entriesKept].Identity == document.Entries[entriesKept].Identity
                ? Prefix(_previous.Entries[entriesKept].Source, document.Entries[entriesKept].Source,
                    static (left, right) => left == right) : 0;
        var delta = new SessionCheckpointRevision(++_sequence,
            entriesKept,
            Prefix(_previous.Cells, document.Cells, SessionCheckpointDelta.CellEquals),
            Prefix(_previous.References, document.References),
            Prefix(_previous.Assets, document.Assets), sourceKept);
        var entries = document.Entries[entriesKept..];
        if (sourceKept != 0)
        {
            entries[0] = entries[0] with { Source = entries[0].Source[sourceKept..] };
        }

        _previous = document;
        return checkpoint with
        {
            CheckpointDelta = delta,
            Document = document with
            {
                Entries = entries, Cells = document.Cells[delta.CellsKept..],
                References = document.References[delta.ReferencesKept..], Assets = document.Assets[delta.AssetsKept..],
            },
        };
    }

    /// <summary>
    /// Reconstructs an exact source revision and rejects missing predecessors before acknowledging user execution.
    /// </summary>
    /// <param name="checkpoint">The received full or incremental checkpoint.</param>
    /// <returns>The expanded revision, or null for an already applied message.</returns>
    public SessionReply? Apply(SessionReply checkpoint)
    {
        if (checkpoint.CheckpointDelta is not { } delta)
        {
            _previous = checkpoint.Document;
            return checkpoint;
        }

        if (delta.Sequence <= _sequence)
        {
            return null;
        }

        if (delta.Sequence != _sequence + 1)
        {
            throw new InvalidDataException("an execution checkpoint predecessor is missing");
        }

        var changes = checkpoint.Document;
        var document = changes with
        {
            Entries = JoinEntries(_previous.Entries, changes.Entries, delta),
            Cells = Join(_previous.Cells, changes.Cells, delta.CellsKept),
            References = Join(_previous.References, changes.References, delta.ReferencesKept),
            Assets = Join(_previous.Assets, changes.Assets, delta.AssetsKept),
        };

        _previous = document;
        _sequence = delta.Sequence;
        return checkpoint with { Document = document, CheckpointDelta = null };
    }

    private static int Prefix<T>(T[] previous, T[] current, Func<T, T, bool>? equals = null) where T : class
    {
        var common = 0;
        while (common < Math.Min(previous.Length, current.Length) && (ReferenceEquals(previous[common], current[common])
            || (equals?.Invoke(previous[common], current[common]) ?? false)))
        {
            common++;
        }

        return common;
    }

    private static SessionEntry[] JoinEntries(SessionEntry[] previous, SessionEntry[] changes, SessionCheckpointRevision delta)
    {
        if (delta.SourceKept == 0)
        {
            return Join(previous, changes, delta.EntriesKept);
        }

        if (delta.SourceKept < 0 || delta.EntriesKept < 0 || delta.EntriesKept >= previous.Length || changes.Length == 0
            || previous[delta.EntriesKept].Identity != changes[0].Identity)
        {
            throw new InvalidDataException("invalid execution checkpoint source prefix");
        }

        var source = Join(previous[delta.EntriesKept].Source, changes[0].Source, delta.SourceKept);
        var entries = Join(previous, changes, delta.EntriesKept);
        entries[delta.EntriesKept] = changes[0] with { Source = source };
        return entries;
    }

    private static T[] Join<T>(T[] previous, T[] changes, int kept)
    {
        if (kept < 0 || kept > previous.Length)
        {
            throw new InvalidDataException("invalid execution checkpoint prefix");
        }

        if (changes.Length == 0 && kept == previous.Length)
        {
            return previous;
        }

        var result = new T[checked(kept + changes.Length)];
        previous.AsSpan(0, kept).CopyTo(result);
        changes.CopyTo(result, kept);
        return result;
    }
}
