namespace IlRepl.Protocol;

/// <summary>
/// Where the session stands, taken before a block is sent so the block can be withdrawn if a
/// line of it is refused. The generation changes whenever something runs, commits, or is
/// discarded, and a mark from an earlier generation cannot be rolled back to: nothing that ran
/// or committed is ever undone. The counts say how many lines each provisional store held, so a
/// rollback keeps everything that came before the block.
/// </summary>
/// <param name="Generation">The session generation the mark belongs to.</param>
/// <param name="BodyLines">How many lines the cell body held.</param>
/// <param name="DeclarationLines">How many declaration lines the cell held.</param>
/// <param name="OpenMethodLines">How many body lines the open <c>.method</c> block held, or null when none was open.</param>
/// <param name="OpenTypeLines">How many lines the open <c>.class</c> family held, or null when none was open.</param>
/// <param name="InBlockComment">Whether a <c>/*</c> comment was open.</param>
public sealed record SessionMark(
    long Generation,
    int BodyLines,
    int DeclarationLines,
    int? OpenMethodLines,
    int? OpenTypeLines,
    bool InBlockComment)
{
    /// <summary>
    /// The mark of a fresh session.
    /// </summary>
    public static SessionMark Initial { get; } = new(0, 0, 0, null, null, false);
}
