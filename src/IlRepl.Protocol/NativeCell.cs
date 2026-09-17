namespace IlRepl.Protocol;

/// <summary>
/// The source and bindings needed to re-emit a cell without running earlier cells.
/// </summary>
public sealed record NativeCell
{
    /// <summary>
    /// The effective argument, local, and generic declarations.
    /// </summary>
    public string[] Declarations { get; init; } = [];

    /// <summary>
    /// The cell instructions and protected regions.
    /// </summary>
    public string[] Body { get; init; } = [];

    /// <summary>
    /// The closed method generic arguments.
    /// </summary>
    public string[] TypeArguments { get; init; } = [];
}
