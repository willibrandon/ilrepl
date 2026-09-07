namespace IlRepl.Engine;

/// <summary>
/// One block line the clause layout places into the listing.
/// </summary>
/// <param name="Offset">The offset the boundary sits before.</param>
/// <param name="Order">The order among boundaries at the same offset: ends first, innermost first; then opens, outermost first.</param>
/// <param name="Kind">The boundary kind.</param>
/// <param name="Clause">The clause the boundary belongs to, for its catch type; null for a <c>.try</c> or an end.</param>
public sealed record BlockBoundary(int Offset, int Order, BlockKind Kind, IlExceptionClause? Clause);
