using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// One CIL opcode as it is encoded: its value, its ILAsm name, how its operand is laid out, and
/// the <see cref="OpCode"/> the emitters use for it when Reflection.Emit has one. The <c>no.</c>
/// prefix (ECMA-335 III.2.2) has no <see cref="OpCode"/>, so a reader that only knew
/// <see cref="OpCodes"/> could not decode it; it is described here like any other instruction.
/// </summary>
/// <param name="Value">The encoded value: the byte for a one-byte opcode, <c>0xFExx</c> for a two-byte one.</param>
/// <param name="Name">The ILAsm name.</param>
/// <param name="OperandType">How the operand is laid out after the opcode.</param>
/// <param name="Emit">The Reflection.Emit opcode, or null when there is none.</param>
public sealed record IlOpcode(ushort Value, string Name, OperandType OperandType, OpCode? Emit)
{
    /// <summary>
    /// The number of bytes the opcode itself takes: two for the <c>0xFE</c> page, one otherwise.
    /// </summary>
    public int Size => Value > 0xFF ? 2 : 1;

    /// <summary>
    /// The number of operand bytes that follow the opcode, or -1 for <c>switch</c>, whose table has
    /// a length of its own.
    /// </summary>
    public int OperandSize => OpcodeTable.OperandSize(OperandType);

    /// <summary>
    /// True when this is <c>no.</c>, whose immediate is an unsigned mask of the checks to skip.
    /// </summary>
    public bool IsSkipChecksPrefix => Value == OpcodeTable.NoPrefixValue;

    /// <inheritdoc/>
    public override string ToString() => Name;
}
