using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// A source transition with an explicit operation that cannot execute arbitrary saved commands.
/// </summary>
public sealed record SessionEntry
{
    /// <summary>
    /// The stable internal identity of this source transition.
    /// </summary>
    public string Identity { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The prompt number associated with this transition.
    /// </summary>
    public int Number { get; set; } = 1;

    /// <summary>
    /// The operation used when reconstructing this source.
    /// </summary>
    public SessionEntryKind Kind { get; set; } = SessionEntryKind.Source;

    /// <summary>
    /// Original physical source lines including comments and formatting.
    /// </summary>
    public string[] Source { get; set; } = [];

    /// <summary>
    /// The referenced manifest entry or edit name, when needed.
    /// </summary>
    public string? Reference { get; set; } = null;

    /// <summary>
    /// The provisional source boundary restored by a rollback.
    /// </summary>
    public SessionMark? Mark { get; set; } = null;

    /// <summary>
    /// The immutable original associated with an edit transition.
    /// </summary>
    public SessionEditSnapshot? Edit { get; set; }

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
