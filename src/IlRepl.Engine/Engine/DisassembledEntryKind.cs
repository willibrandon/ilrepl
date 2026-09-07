namespace IlRepl.Engine;

/// <summary>
/// The kinds of line a disassembly listing holds.
/// </summary>
public enum DisassembledEntryKind
{
    /// <summary>
    /// A label line, <c>IL_0010:</c>.
    /// </summary>
    Label,

    /// <summary>
    /// An instruction the simulator can apply.
    /// </summary>
    Instruction,

    /// <summary>
    /// A block boundary: <c>.try {</c>, <c>} catch T {</c>, <c>}</c>.
    /// </summary>
    Block,

    /// <summary>
    /// An instruction printed as text only: an operand that did not resolve, or <c>no.</c>.
    /// </summary>
    Raw,
}
