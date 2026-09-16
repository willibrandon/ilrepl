using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// An editable experiment containing source and dependency identities, never serialized runtime objects.
/// </summary>
public sealed record SessionDocument
{
    /// <summary>
    /// The stable document format identifier.
    /// </summary>
    public string Format { get; set; } = "ilrepl-session";

    /// <summary>
    /// The supported document format version.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// The runtime on which the document was captured.
    /// </summary>
    public SessionRuntime Runtime { get; set; } = new();

    /// <summary>
    /// The ordered, typed source transitions required to reconstruct the experiment.
    /// </summary>
    public SessionEntry[] Entries { get; set; } = [];

    /// <summary>
    /// Numbered submissions and their historical execution output.
    /// </summary>
    public SessionCell[] Cells { get; set; } = [];

    /// <summary>
    /// Interrupted attempts retained from the last acknowledged execution checkpoints.
    /// </summary>
    public SessionInterruption[] Interruptions { get; set; } = [];

    /// <summary>
    /// The unsent editor text and selection.
    /// </summary>
    public SessionEditor Editor { get; set; } = new();

    /// <summary>
    /// The requested and resolved external references.
    /// </summary>
    public SessionReference[] References { get; set; } = [];

    /// <summary>
    /// Deduplicated embedded images keyed by their content hashes.
    /// </summary>
    public SessionAsset[] Assets { get; set; } = [];

    /// <summary>
    /// Standard NuGet dependency lock data, when packages were resolved.
    /// </summary>
    public string? PackageLock { get; set; } = null;

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
