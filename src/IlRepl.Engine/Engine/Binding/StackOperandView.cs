using System.Reflection.Emit;

namespace IlRepl.Engine.Binding;

/// <summary>
/// What the stack transfer needs to know about one instruction, whatever its operand is made of:
/// the opcode, the types the operand names, how many arguments a call pops, and which slot a
/// load reads. The runtime stack model builds it from an <see cref="Instruction"/>; a preview
/// builds it from a <see cref="BoundInstruction"/>.
/// </summary>
/// <typeparam name="T">The type representation.</typeparam>
public sealed record StackOperandView<T> where T : class
{
    /// <summary>
    /// The opcode.
    /// </summary>
    public required OpCode Op { get; init; }

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
    /// The type <c>newobj</c> constructs.
    /// </summary>
    public T? DeclaringType { get; init; }

    /// <summary>
    /// The type of the field a field opcode reads or writes.
    /// </summary>
    public T? FieldType { get; init; }

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
}
