namespace IlRepl.Tests.Engine;

/// <summary>
/// A mutable value supplies real back-edges and aliases inside an immutable dictionary.
/// </summary>
internal sealed class ImmutableObservationLink
{
    /// <summary>
    /// Refers back to the containing immutable collection.
    /// </summary>
    public object? Parent;

    /// <summary>
    /// Refers to this node or another value in the same collection.
    /// </summary>
    public object? Other;
}
