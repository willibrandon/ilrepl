namespace IlRepl.Protocol;

/// <summary>
/// A snapshot of the session for the status bar and the prompt. The stack, locals, instructions,
/// and open blocks describe the body being written: the cell, the open <c>.method</c> block, or
/// the open member of a <c>.class</c> block. When both a type and a method are open, the method
/// is inside the type.
/// </summary>
/// <param name="Prompt">The prompt for the next line, for example <c>il[3]&gt; </c>.</param>
/// <param name="CellNumber">The number of the cell being written. A run and a committed method block each complete one.</param>
/// <param name="Stack">The rendered stack, for example <c>[int32, string]</c>.</param>
/// <param name="StackDepth">The number of values on the stack.</param>
/// <param name="Locals">The number of declared locals.</param>
/// <param name="Instructions">The number of instructions in the body.</param>
/// <param name="OpenBlocks">The number of open protected regions.</param>
/// <param name="CellIsEmpty">True when the body has no instructions.</param>
/// <param name="OpenMethod">The name of the method block being written, or null when lines go to the cell.</param>
/// <param name="Methods">The number of methods defined so far.</param>
/// <param name="OpenType">The ILAsm path of the innermost type block being written, such as <c>Outer/Inner</c>, or null.</param>
/// <param name="Types">The number of types defined so far, nested ones included.</param>
/// <param name="Mark">Where the session stands, for withdrawing a block a line of which is refused.</param>
/// <param name="OpenDepth">How many closing braces the engine is waiting for: open regions, methods, and types together.</param>
/// <param name="Revision">Tracks every completion dependency change, including changes otherwise equal statuses cannot show.</param>
public sealed record SessionStatus(
    string Prompt,
    int CellNumber,
    string Stack,
    int StackDepth,
    int Locals,
    int Instructions,
    int OpenBlocks,
    bool CellIsEmpty,
    string? OpenMethod,
    int Methods,
    string? OpenType,
    int Types,
    SessionMark Mark,
    int OpenDepth,
    long Revision = 0)
{
    /// <summary>
    /// The status of a fresh session.
    /// </summary>
    public static SessionStatus Initial { get; } = new("il[1]> ", 1, "[]", 0, 0, 0, 0, true, null, 0, null, 0, SessionMark.Initial, 0);
}
