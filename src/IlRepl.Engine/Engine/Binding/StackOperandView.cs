using System.Reflection.Emit;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Provides the opcode and operand facts required by stack-transfer rules.
/// </summary>
/// <remarks>
/// What the stack transfer needs to know about one instruction, whatever its operand is made of:
/// the opcode, the types the operand names, how many arguments a call pops, and which slot a
/// load reads. The runtime stack model builds it from an <see cref="Instruction"/>; a preview
/// builds it from a <see cref="BoundInstruction"/>.
/// </remarks>
/// <typeparam name="T">The type representation.</typeparam>
public sealed record StackOperandView<T> where T : class
{
    /// <summary>
    /// The opcode.
    /// </summary>
    public required OpCode Op { get; init; }

    /// <summary>
    /// The byte operand of a prefix such as <c>unaligned.</c>, or null for other instructions.
    /// </summary>
    public byte? ByteOperand { get; init; }

    /// <summary>
    /// For an inline <c>ret</c>: how many values it pops.
    /// </summary>
    public int RetPops { get; init; }

    /// <summary>
    /// The type a type operand names, for <c>box</c>, <c>newarr</c>, <c>castclass</c>, and the rest.
    /// </summary>
    public T? Type { get; init; }

    /// <summary>
    /// The return type of a call or <c>calli</c>; null when it returns nothing.
    /// </summary>
    public T? ReturnType { get; init; }

    /// <summary>
    /// How many values a call or <c>calli</c> pops for its arguments and receiver.
    /// </summary>
    public int ArgumentPops { get; init; }

    /// <summary>
    /// The fixed and optional parameters of a call, excluding the receiver and function pointer.
    /// </summary>
    public IReadOnlyList<T> ParameterTypes { get; init; } = [];

    /// <summary>
    /// Whether a method operand consumes an instance receiver.
    /// </summary>
    public bool IsInstance { get; init; }

    /// <summary>
    /// Whether a method operand names a static method; null when the instruction has no method operand.
    /// </summary>
    public bool? MethodIsStatic { get; init; }

    /// <summary>
    /// The type <c>newobj</c> constructs.
    /// </summary>
    public T? DeclaringType { get; init; }

    /// <summary>
    /// The type of the field a field opcode reads or writes.
    /// </summary>
    public T? FieldType { get; init; }

    /// <summary>
    /// Whether a field operand names static storage; null when the instruction has no field operand.
    /// </summary>
    public bool? FieldIsStatic { get; init; }

    /// <summary>
    /// Explains why this field store is forbidden in the enclosing method.
    /// </summary>
    public string? StoreRestriction { get; init; }

    /// <summary>
    /// Explains why this field store requires the original instance receiver on every incoming path.
    /// </summary>
    public string? ReceiverRestriction { get; init; }

    /// <summary>
    /// What an <c>ldtoken</c> names.
    /// </summary>
    public StackTokenKind Token { get; init; } = StackTokenKind.Method;

    /// <summary>
    /// The type of the local or argument a load reads.
    /// </summary>
    public T? SlotType { get; init; }

    /// <summary>
    /// True when the instruction loads <c>this</c>.
    /// </summary>
    public bool LoadsThis { get; init; }

    /// <summary>
    /// True when the instruction loads the address of <c>this</c>.
    /// </summary>
    public bool AddressOfThis { get; init; }

    /// <summary>
    /// Explains why a <c>jmp</c> target is incompatible with the enclosing method.
    /// </summary>
    public string? JumpRestriction { get; init; }
}
