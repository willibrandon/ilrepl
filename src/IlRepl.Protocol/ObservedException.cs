namespace IlRepl.Protocol;

/// <summary>
/// An exception observation without invocation wrappers or user-defined formatting.
/// </summary>
/// <param name="Type">The exception type identity.</param>
/// <param name="Message">The stored exception message.</param>
/// <param name="HResult">The numeric failure code.</param>
/// <param name="Inner">The observed inner exception, or null.</param>
public sealed record ObservedException(string Type, string? Message, int HResult, ObservedException? Inner)
{
    /// <summary>
    /// The remaining aggregate children in order, after the first child stored in Inner.
    /// </summary>
    public IReadOnlyList<ObservedException> AdditionalInnerExceptions { get; init; } = [];

    /// <summary>
    /// The reason this exception could not be observed completely, or null for a complete observation.
    /// </summary>
    public string? Problem { get; init; }

    /// <summary>
    /// Compares the complete exception tree, including every aggregate child in its original order.
    /// </summary>
    /// <param name="other">The observation to compare.</param>
    /// <returns>Whether both observations contain the same exception data.</returns>
    public bool Equals(ObservedException? other) => ReferenceEquals(this, other)
        || other is not null && Type == other.Type && Message == other.Message && HResult == other.HResult
            && Inner == other.Inner && Problem == other.Problem
            && AdditionalInnerExceptions.SequenceEqual(other.AdditionalInnerExceptions);

    /// <summary>
    /// Computes a hash from the same ordered exception data used by equality.
    /// </summary>
    /// <returns>The structural hash code.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type, StringComparer.Ordinal);
        hash.Add(Message, StringComparer.Ordinal);
        hash.Add(HResult);
        hash.Add(Inner);
        hash.Add(Problem, StringComparer.Ordinal);
        foreach (var child in AdditionalInnerExceptions) hash.Add(child);
        return hash.ToHashCode();
    }
}
