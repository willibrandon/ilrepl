using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// What kind of operand an opcode takes, as far as colouring it goes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CilOperandKind>))]
public enum CilOperandKind
{
    /// <summary>
    /// No operand.
    /// </summary>
    None,

    /// <summary>
    /// A label to branch to.
    /// </summary>
    Branch,

    /// <summary>
    /// A parenthesised list of labels.
    /// </summary>
    Switch,

    /// <summary>
    /// An integer, or a character in quotes.
    /// </summary>
    Integer,

    /// <summary>
    /// A floating-point number, <c>nan</c>, <c>inf</c>, or <c>float32(...)</c> bits.
    /// </summary>
    Float,

    /// <summary>
    /// A local or argument, by name or index.
    /// </summary>
    Variable,

    /// <summary>
    /// A string literal.
    /// </summary>
    String,

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
    /// A type, or <c>method</c> or <c>field</c> followed by a reference.
    /// </summary>
    Token,

    /// <summary>
    /// A call-site signature.
    /// </summary>
    Signature,
}
