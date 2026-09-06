namespace IlRepl.Protocol;

/// <summary>
/// A snapshot of the session for the status bar and the prompt.
/// </summary>
/// <param name="Prompt">The prompt for the next line, for example <c>il[3]&gt; </c>.</param>
/// <param name="CellNumber">The number of the cell being written.</param>
/// <param name="Stack">The rendered stack, for example <c>[int32, string]</c>.</param>
/// <param name="StackDepth">The number of values on the stack.</param>
/// <param name="Locals">The number of declared locals.</param>
/// <param name="Instructions">The number of instructions in the cell.</param>
/// <param name="OpenBlocks">The number of open protected regions.</param>
/// <param name="CellIsEmpty">True when the cell has no instructions.</param>
public sealed record SessionStatus(
    string Prompt,
    int CellNumber,
    string Stack,
    int StackDepth,
    int Locals,
    int Instructions,
    int OpenBlocks,
    bool CellIsEmpty)
{
    /// <summary>
    /// The status of a fresh session.
    /// </summary>
    public static SessionStatus Initial { get; } = new("il[1]> ", 1, "[]", 0, 0, 0, 0, true);
}
