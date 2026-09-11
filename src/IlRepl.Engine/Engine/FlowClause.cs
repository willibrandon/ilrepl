namespace IlRepl.Engine;

/// <summary>
/// Identifies one ordered exception clause and its filter or handler entry points.
/// </summary>
internal sealed record FlowClause(int Group, BlockKind Kind, int Entry, int Handler);
