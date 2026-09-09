using System.Reflection.Emit;

namespace IlRepl.Engine.Binding;

/// <summary>
/// One instruction bound to symbols: the opcode, its operand, and the slot it reads or writes.
/// </summary>
/// <param name="Op">The opcode.</param>
/// <param name="Text">The instruction as typed, without labels or comments.</param>
/// <param name="Operand">The operand.</param>
/// <param name="LocalIndex">The local the instruction reads or writes, for short forms and explicit operands.</param>
/// <param name="ArgumentIndex">The argument the instruction reads or writes.</param>
public sealed record BoundInstruction(OpCode Op, string Text, BoundOperand Operand, int? LocalIndex, int? ArgumentIndex);
