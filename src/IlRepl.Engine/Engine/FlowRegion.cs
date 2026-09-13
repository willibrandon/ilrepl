namespace IlRepl.Engine;

/// <summary>
/// Records a protected section's bounds and the exception group it belongs to.
/// </summary>
internal sealed record FlowRegion(BlockKind Kind, int Start, int End, int Group);
