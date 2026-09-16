using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// A numbered source submission whose output is historical text rather than a runtime value.
/// </summary>
public sealed record SessionCell
{
    /// <summary>
    /// The stable internal source identity.
    /// </summary>
    public string Identity { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The original prompt number used by recall and explicit execution.
    /// </summary>
    public int Number { get; set; } = 1;

    /// <summary>
    /// Whether this submission executes a cell or commits a definition.
    /// </summary>
    public string Kind { get; set; } = "cell";

    /// <summary>
    /// The source submitted at this prompt.
    /// </summary>
    public string[] Source { get; set; } = [];

    /// <summary>
    /// The argument, local, and generic declarations in effect for this cell.
    /// </summary>
    public string[] Inputs { get; set; } = [];

    /// <summary>
    /// The historical execution state: unrun, succeeded, failed, or interrupted.
    /// </summary>
    public string State { get; set; } = "unrun";

    /// <summary>
    /// Historical output, errors, and formatted result lines.
    /// </summary>
    public TranscriptLine[] Output { get; set; } = [];

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
