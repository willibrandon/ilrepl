namespace IlRepl.Engine;

/// <summary>
/// What the clause layout decided: the block lines it can draw with braces and the clauses it cannot.
/// </summary>
/// <remarks>
/// It also holds the offsets those clauses refer to, which need labels.
/// </remarks>
/// <param name="Boundaries">The block lines, sorted by offset then order.</param>
/// <param name="Fallback">The clauses printed in offset form.</param>
/// <param name="ReferencedOffsets">Every boundary a fallback clause names, the code size included.</param>
public sealed record ClauseLayoutResult(
    IReadOnlyList<BlockBoundary> Boundaries,
    IReadOnlyList<IlExceptionClause> Fallback,
    IReadOnlySet<int> ReferencedOffsets);
