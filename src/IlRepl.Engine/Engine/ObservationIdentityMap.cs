using System.Runtime.CompilerServices;

namespace IlRepl.Engine;

/// <summary>
/// Retains reference identities between snapshots without keeping observed objects alive.
/// </summary>
/// <param name="previous">The completed earlier snapshot, or null for the first snapshot.</param>
internal sealed class ObservationIdentityMap(ObservationIdentityMap? previous = null)
{
    private readonly ConditionalWeakTable<object, StrongBox<int>> _identities = new();
    private int _nextIdentity = previous?._nextIdentity ?? 0;

    /// <summary>
    /// Returns an earlier object's identity or assigns the next identity to a newly observed object.
    /// </summary>
    /// <param name="value">The observed reference, including an existing box.</param>
    /// <returns>The identity shared with earlier snapshots when the same object is still present.</returns>
    internal int Get(object value)
    {
        if (TryGet(value, out var identity)) return identity;
        identity = ++_nextIdentity;
        _identities.Add(value, new StrongBox<int>(identity));
        return identity;
    }

    private bool TryGet(object value, out int identity)
    {
        if (_identities.TryGetValue(value, out var entry))
        {
            identity = entry.Value;
            return true;
        }

        if (previous is not null) return previous.TryGet(value, out identity);
        identity = 0;
        return false;
    }
}
