using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// The unsent editor document, kept separately from accepted engine source.
/// </summary>
public sealed record SessionEditor
{
    /// <summary>
    /// The original physical lines in the editor.
    /// </summary>
    public string[] Lines { get; set; } = [];

    /// <summary>
    /// The caret offset within the text joined with line feeds.
    /// </summary>
    public int Caret { get; set; } = 0;

    /// <summary>
    /// The selection anchor offset, equal to the caret when no selection exists.
    /// </summary>
    public int Anchor { get; set; } = 0;

    /// <summary>
    /// The frontend revision of this editor snapshot.
    /// </summary>
    public long Revision { get; set; } = 0;

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
