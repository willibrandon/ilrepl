using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// What a transcript line represents.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LineKind>))]
public enum LineKind
{
    /// <summary>
    /// A line the user typed, shown with its prompt.
    /// </summary>
    Input,

    /// <summary>
    /// The stack echo after an instruction.
    /// </summary>
    Stack,

    /// <summary>
    /// The value a cell produced.
    /// </summary>
    Result,

    /// <summary>
    /// An error message.
    /// </summary>
    Error,

    /// <summary>
    /// Text the cell wrote to the console.
    /// </summary>
    Output,

    /// <summary>
    /// A note from a command.
    /// </summary>
    Info,

    /// <summary>
    /// A line of help or a listing.
    /// </summary>
    Listing,
}
