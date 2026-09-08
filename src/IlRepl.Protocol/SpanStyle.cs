using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// How a span of transcript text is drawn. The terminal UI maps each style to a colour and the
/// batch writer to an ANSI code; over the wire the name travels as a string.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpanStyle>))]
public enum SpanStyle
{
    /// <summary>
    /// Plain text.
    /// </summary>
    Default,

    /// <summary>
    /// Text of secondary importance: notes, offsets, the stack column.
    /// </summary>
    Dim,

    /// <summary>
    /// The prompt.
    /// </summary>
    Prompt,

    /// <summary>
    /// Text the user typed that has no more specific style.
    /// </summary>
    Input,

    /// <summary>
    /// An opcode.
    /// </summary>
    Opcode,

    /// <summary>
    /// A type.
    /// </summary>
    Type,

    /// <summary>
    /// The type on top of the stack.
    /// </summary>
    TopType,

    /// <summary>
    /// A label.
    /// </summary>
    Label,

    /// <summary>
    /// A number.
    /// </summary>
    Number,

    /// <summary>
    /// A string literal.
    /// </summary>
    String,

    /// <summary>
    /// An ILAsm keyword such as <c>instance</c> or <c>cil managed</c>, or a null or boolean value.
    /// </summary>
    Keyword,

    /// <summary>
    /// An error.
    /// </summary>
    Error,

    /// <summary>
    /// A command.
    /// </summary>
    Command,

    /// <summary>
    /// Output the cell wrote.
    /// </summary>
    Output,

    /// <summary>
    /// A heading.
    /// </summary>
    Heading,

    /// <summary>
    /// A directive such as <c>.locals</c> or <c>.method</c>.
    /// </summary>
    Directive,

    /// <summary>
    /// A comment.
    /// </summary>
    Comment,

    /// <summary>
    /// A method, field, property, or event name.
    /// </summary>
    Member,

    /// <summary>
    /// Punctuation: parentheses, brackets, commas, and the <c>::</c> between a type and its member.
    /// </summary>
    Punctuation,
}
