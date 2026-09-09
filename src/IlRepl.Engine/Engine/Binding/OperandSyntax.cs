namespace IlRepl.Engine.Binding;

/// <summary>
/// An instruction's operand as written, with the syntax of a type, member, or signature operand
/// already read and the text of a literal kept for the binder to check against its opcode.
/// </summary>
public sealed record OperandSyntax
{
    /// <summary>
    /// The shape.
    /// </summary>
    public required OperandSyntaxKind Kind { get; init; }

    /// <summary>
    /// The operand text, trimmed.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// The index of the first character of the operand in the instruction text.
    /// </summary>
    public int Start { get; init; }

    /// <summary>
    /// The index after the last character of the operand.
    /// </summary>
    public int End { get; init; }

    /// <summary>
    /// True for an argument operand, <c>ldarg</c>/<c>starg</c>; false for a local.
    /// </summary>
    public bool IsArgument { get; init; }

    /// <summary>
    /// The labels of a <c>switch</c>.
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>
    /// The type of a type operand or a type token.
    /// </summary>
    public TypeSyntax? Type { get; init; }

    /// <summary>
    /// The member of a method, field, or member token operand.
    /// </summary>
    public MemberSyntax? Member { get; init; }

    /// <summary>
    /// True when a token operand names a field, after the <c>field</c> word.
    /// </summary>
    public bool IsFieldToken { get; init; }

    /// <summary>
    /// True when a token operand names a method, after the <c>method</c> word.
    /// </summary>
    public bool IsMethodToken { get; init; }

    /// <summary>
    /// The signature of a <c>calli</c> operand.
    /// </summary>
    public SignatureSyntax? Signature { get; init; }
}
