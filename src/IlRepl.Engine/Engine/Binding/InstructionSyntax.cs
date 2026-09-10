using System.Reflection.Emit;

namespace IlRepl.Engine.Binding;

/// <summary>
/// One instruction as written, labels and comments already removed: its opcode and its operand syntax.
/// </summary>
/// <param name="Op">The opcode.</param>
/// <param name="Text">The instruction text, trimmed.</param>
/// <param name="MnemonicEnd">The index after the mnemonic.</param>
/// <param name="Operand">The operand.</param>
public sealed record InstructionSyntax(OpCode Op, string Text, int MnemonicEnd, OperandSyntax Operand);
