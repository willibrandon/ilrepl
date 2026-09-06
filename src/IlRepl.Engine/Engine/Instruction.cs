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
}
