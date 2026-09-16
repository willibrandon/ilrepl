using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// A classified session operation for frontend coordination without parsing command text again.
/// </summary>
public sealed record SessionAction
{
    /// <summary>
    /// The requested workspace operation.
    /// </summary>
    public SessionOperation Operation { get; init; } = SessionOperation.Summary;

    /// <summary>
    /// The user-supplied path or reference request.
    /// </summary>
    public string? Path { get; init; } = null;

    /// <summary>
    /// Whether source replacement was explicitly requested without a dirty dialog.
    /// </summary>
    public bool Force { get; init; } = false;

    /// <summary>
    /// Explicitly executes a session file in the fresh host receiving the open request.
    /// </summary>
    public bool Execute { get; init; } = false;

    /// <summary>
    /// Whether a save includes available non-framework dependencies.
    /// </summary>
    public bool Embed { get; init; } = false;

    /// <summary>
    /// Whether dependency recovery may evaluate and build projects.
    /// </summary>
    public bool Build { get; init; } = false;

    /// <summary>
    /// Whether reference loading explicitly requests a fresh execution environment.
    /// </summary>
    public bool Reload { get; init; } = false;

    /// <summary>
    /// Whether a project load uses existing build outputs.
    /// </summary>
    public bool NoBuild { get; init; } = false;

    /// <summary>
    /// The explicitly requested project framework.
    /// </summary>
    public string? Framework { get; init; } = null;

    /// <summary>
    /// The explicitly requested project configuration.
    /// </summary>
    public string? Configuration { get; init; } = null;

    /// <summary>
    /// The selected prompt numbers, or an empty array for run-all.
    /// </summary>
    public int[] Numbers { get; init; } = [];

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
