namespace IlRepl.Engine.Binding;

/// <summary>
/// An instruction's operand once bound: a literal, a label, a slot index, or a symbol.
/// </summary>
public sealed record BoundOperand
{
    /// <summary>
    /// The operand kind the emitter dispatches on.
    /// </summary>
    public required OperandKind Kind { get; init; }

    /// <summary>
    /// A boxed literal, a label name, a label array, or a slot index; null for a symbol operand.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// The type of a type operand or a type token.
    /// </summary>
    public TypeSymbol? Type { get; init; }

    /// <summary>
    /// The member of a method operand or a method token.
    /// </summary>
    public BoundMethod? Method { get; init; }

    /// <summary>
    /// The field of a field operand or a field token.
    /// </summary>
    public FieldSymbol? Field { get; init; }

    /// <summary>
    /// The signature of a <c>calli</c>.
    /// </summary>
    public MethodSignatureSymbol? Signature { get; init; }

    /// <summary>
    /// No operand.
    /// </summary>
    public static BoundOperand None { get; } = new() { Kind = OperandKind.None };
}
