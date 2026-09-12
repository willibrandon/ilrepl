namespace IlRepl.Engine;

/// <summary>
/// Identifies one ordered exception clause and its filter or handler entry points.
/// </summary>
/// <param name="Group">The protected-region group.</param>
/// <param name="Kind">The clause kind.</param>
/// <param name="Entry">The filter or handler entry.</param>
/// <param name="Handler">The handler entry.</param>
/// <param name="CatchesAll">Whether the clause catches every exception.</param>
internal sealed record FlowClause(int Group, BlockKind Kind, int Entry, int Handler,
    bool CatchesAll = false);
