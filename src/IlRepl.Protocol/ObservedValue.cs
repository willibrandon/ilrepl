namespace IlRepl.Protocol;

/// <summary>
/// A structural value observation that preserves typed scalar bits, cycles, and shared references.
/// </summary>
/// <param name="Kind">
/// Null, null-reference, null-task, scalar, object, array, dictionary, set, entry, comparer, reference, or unavailable.
/// </param>
/// <param name="Type">The logical metadata type identity.</param>
/// <param name="Value">A scalar representation or an explanation for an unavailable observation.</param>
/// <param name="Identity">The reference identity within a graph and linked before/after snapshots, or null when unavailable.</param>
/// <param name="Members">The observed fields, array elements, or collection entries in traversal order.</param>
public sealed record ObservedValue(string Kind, string Type, string? Value, int? Identity, IReadOnlyList<ObservedMember> Members)
{
    /// <summary>
    /// Compares the captured fields and references without invoking equality on the original objects.
    /// </summary>
    /// <param name="other">The observation to compare.</param>
    /// <returns>Whether both observations contain the same structural data.</returns>
    public bool Equals(ObservedValue? other) => ReferenceEquals(this, other)
        || other is not null && Kind == other.Kind && Type == other.Type && Value == other.Value && Identity == other.Identity
            && Members.SequenceEqual(other.Members);

    /// <summary>
    /// Computes a hash from the same ordered structural data used by equality.
    /// </summary>
    /// <returns>The structural hash code.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind, StringComparer.Ordinal);
        hash.Add(Type, StringComparer.Ordinal);
        hash.Add(Value, StringComparer.Ordinal);
        hash.Add(Identity);
        foreach (var member in Members)
        {
            hash.Add(member);
        }

        return hash.ToHashCode();
    }
}
