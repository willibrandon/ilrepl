namespace IlRepl.Engine;

/// <summary>
/// One instruction as it sits in a method body: where it starts, how many bytes it takes, its
/// opcode, and its operand as read.
/// </summary>
/// <param name="Offset">The offset of the first byte of the opcode.</param>
/// <param name="Size">The opcode and operand bytes together.</param>
/// <param name="Op">The opcode.</param>
/// <param name="Operand">The operand.</param>
public sealed record RawInstruction(int Offset, int Size, IlOpcode Op, RawOperand Operand)
{
    /// <summary>
    /// The offset just after this instruction, which branch operands are relative to.
    /// </summary>
    public int Next => Offset + Size;

    /// <summary>
    /// The absolute target of a branch or <c>leave</c>, or null for every other instruction.
    /// </summary>
    public int? BranchTarget { get; init; }

    /// <summary>
    /// The offset as a label, <c>IL_0004</c>.
    /// </summary>
    public string Label => IlReader.LabelFor(Offset);
}
