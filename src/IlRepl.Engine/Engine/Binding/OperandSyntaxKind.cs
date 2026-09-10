namespace IlRepl.Engine.Binding;

/// <summary>
/// The shape of an instruction's operand as written, decided by the opcode.
/// </summary>
public enum OperandSyntaxKind
{
    /// <summary>
    /// No operand is expected; the text after the mnemonic, if any, is kept for the binder to refuse.
    /// </summary>
    None,

    /// <summary>
    /// An integer literal.
    /// </summary>
    Integer,

    /// <summary>
    /// A floating-point literal.
    /// </summary>
    Float,

    /// <summary>
    /// A string literal.
    /// </summary>
    String,

    /// <summary>
    /// One branch target label.
    /// </summary>
    Label,

    /// <summary>
    /// A <c>switch</c> table of labels.
    /// </summary>
    Labels,

    /// <summary>
    /// A local or argument, by name or index.
    /// </summary>
    Variable,

    /// <summary>
    /// A type.
    /// </summary>
    Type,

    /// <summary>
    /// A method reference.
    /// </summary>
    Member,

    /// <summary>
    /// A field reference.
    /// </summary>
    Field,

    /// <summary>
    /// An <c>ldtoken</c> operand: a type, or a member after <c>method</c> or <c>field</c>.
    /// </summary>
    Token,

    /// <summary>
    /// A <c>calli</c> signature.
    /// </summary>
    Signature,
}
