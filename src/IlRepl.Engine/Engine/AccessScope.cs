namespace IlRepl.Engine;

/// <summary>
/// Identifies the body owner and diagnostic name used when checking member access.
/// </summary>
/// <param name="Type">The prototype or runtime type of the body's owner, or null.</param>
/// <param name="Description">How messages name the accessor: <c>the cell</c>, <c>method Twice</c>, <c>class Line</c>.</param>
public sealed record AccessScope(Type? Type, string Description)
{
    /// <summary>
    /// The cell.
    /// </summary>
    public static AccessScope Cell { get; } = new(null, "the cell");
}
