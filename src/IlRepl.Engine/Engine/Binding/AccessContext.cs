namespace IlRepl.Engine.Binding;

/// <summary>
/// Identifies the type and diagnostic name of the context requesting access.
/// </summary>
/// <remarks>
/// Where an access happens, as a symbol: the type whose body is being written, or null for the
/// cell and for session methods, and how messages name the accessor.
/// </remarks>
/// <param name="Type">The type the body belongs to, or null.</param>
/// <param name="Description">How messages name the accessor: <c>the cell</c>, <c>method Twice</c>, <c>class Line</c>.</param>
public sealed record AccessContext(TypeSymbol? Type, string Description)
{
    /// <summary>
    /// The cell.
    /// </summary>
    public static AccessContext Cell { get; } = new(null, "the cell");
}
