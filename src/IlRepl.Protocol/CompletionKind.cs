using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// The source of an operand completion page.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CompletionKind>))]
public enum CompletionKind
{
    /// <summary>
    /// No operand site.
    /// </summary>
    None,
    /// <summary>
    /// Type names.
    /// </summary>
    Types,
    /// <summary>
    /// Members of a declaring type.
    /// </summary>
    Members,
    /// <summary>
    /// Fields.
    /// </summary>
    Fields,
    /// <summary>
    /// Session methods.
    /// </summary>
    Methods,
    /// <summary>
    /// Local slots.
    /// </summary>
    Locals,
    /// <summary>
    /// Argument slots.
    /// </summary>
    Arguments,
    /// <summary>
    /// Labels of the current body.
    /// </summary>
    Labels,
    /// <summary>
    /// In-scope generic parameters.
    /// </summary>
    GenericParameters,
    /// <summary>
    /// Arguments of a generic construction.
    /// </summary>
    TypeArguments,
    /// <summary>
    /// Parameter lists of a selected generic method.
    /// </summary>
    Signatures,
}
