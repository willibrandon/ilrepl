using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// The allowlisted runtime information recorded with an experiment.
/// </summary>
public sealed record SessionRuntime
{
    /// <summary>
    /// The version of ilrepl that captured the document.
    /// </summary>
    public string IlreplVersion { get; set; } = "";

    /// <summary>
    /// The target framework used for dependency selection.
    /// </summary>
    public string Framework { get; set; } = "";

    /// <summary>
    /// The runtime implementation and version.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// The runtime identifier used for asset selection.
    /// </summary>
    public string Rid { get; set; } = "";

    /// <summary>
    /// The operating system description.
    /// </summary>
    public string OperatingSystem { get; set; } = "";

    /// <summary>
    /// The process architecture.
    /// </summary>
    public string Architecture { get; set; } = "";

    /// <summary>
    /// The culture used when the experiment was recorded.
    /// </summary>
    public string Culture { get; set; } = "";

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
