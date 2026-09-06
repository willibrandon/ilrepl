using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// The role of a piece of transcript text. The terminal UI and the batch writer map each role
/// to a color.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpanStyle>))]
public enum SpanStyle
{
    /// <summary>
    /// Plain text.
    /// </summary>
    Default,

    /// <summary>
    /// De-emphasized text such as stack echoes and notes.
    /// </summary>
    Dim,

    /// <summary>
    /// The prompt label.
    /// </summary>
    Prompt,

    /// <summary>
    /// Text the user typed.
    /// </summary>
    Input,

    /// <summary>
    /// An opcode name.
    /// </summary>
    Opcode,

    /// <summary>
    /// A type name on the stack.
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
    /// A string or character literal.
    /// </summary>
    String,

    /// <summary>
    /// A null or a boolean.
    /// </summary>
    Keyword,

    /// <summary>
    /// An error.
    /// </summary>
    Error,

    /// <summary>
    /// A REPL command name.
    /// </summary>
    Command,

    /// <summary>
    /// Text the cell wrote to standard output.
    /// </summary>
    Output,

    /// <summary>
    /// A heading in help output.
    /// </summary>
    Heading,
}
