using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// One parsed IL instruction: the opcode, its operand, and the details the emitter and the
/// stack simulator need.
/// </summary>
public sealed class Instruction
{
    /// <summary>
    /// The opcode.
    /// </summary>
    public required OpCode Op { get; init; }

    /// <summary>
    /// The instruction as the user typed it, without labels or comments.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// The operand kind.
    /// </summary>
    public OperandKind Kind { get; init; }

    /// <summary>
    /// The operand: a boxed immediate, a string, a label name or names, a local or argument
    /// index, a <see cref="Type"/>, a <see cref="ResolvedMethod"/>, a field, or a <see cref="CalliSignature"/>.
    /// </summary>
    public object? Operand { get; init; }

    /// <summary>
    /// The local this instruction reads or writes, for both short forms and explicit operands.
    /// </summary>
    public int? LocalIndex { get; init; }

    /// <summary>
    /// The cell argument this instruction reads or writes.
    /// </summary>
    public int? ArgumentIndex { get; init; }

    /// <summary>
    /// For an inline <c>ret</c>: how many values it pops, 0 on a void path or 1 when returning a value.
    /// </summary>
    public int RetPops { get; init; }

    /// <summary>
    /// For an inline <c>ret</c>: the value type to box before returning, or null.
    /// </summary>
    public Type? RetBox { get; init; }

    /// <summary>
    /// For an inline <c>ret</c> in the cell: true when the stack is empty and <c>ldnull</c> must be
    /// pushed first, because the cell method returns <c>object</c>. Never set inside a <c>.method</c>.
    /// </summary>
    public bool RetNull { get; init; }

    /// <summary>
    /// True when nothing after this instruction is reachable on the same path: a return, a throw,
    /// an unconditional branch, or a jump.
    /// </summary>
    public bool EndsFlow => Op.FlowControl is FlowControl.Return or FlowControl.Throw || Op == OpCodes.Br || Op == OpCodes.Br_S || Op == OpCodes.Jmp;
}
