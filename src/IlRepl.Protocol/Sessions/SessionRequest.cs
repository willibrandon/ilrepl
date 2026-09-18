using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// A typed workspace request shared by the host, terminal, and browser.
/// </summary>
public sealed record SessionRequest
{
    /// <summary>
    /// The requested operation and its options.
    /// </summary>
    public SessionAction Action { get; init; } = new();

    /// <summary>
    /// The matching frontend editor snapshot.
    /// </summary>
    public SessionEditor Editor { get; init; } = new();

    /// <summary>
    /// The document supplied for reconstruction or explicit replay.
    /// </summary>
    public SessionDocument? Document { get; init; } = null;

    /// <summary>
    /// The associated document path retained when a dependency or execution operation replaces the runtime.
    /// </summary>
    public string? AssociatedPath { get; init; }

    /// <summary>
    /// Whether reconstruction represents an unsaved change rather than opening a saved document.
    /// </summary>
    public bool Modified { get; init; }

    /// <summary>
    /// Whether reconstruction should announce an explicitly opened session rather than silently recover a checkpoint.
    /// </summary>
    public bool AnnounceOpen { get; init; } = true;

    /// <summary>
    /// The maximum historical presentation rows, with zero unlimited and null using the frontend's configured limit.
    /// </summary>
    public int? HistoryLineLimit { get; init; }

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
