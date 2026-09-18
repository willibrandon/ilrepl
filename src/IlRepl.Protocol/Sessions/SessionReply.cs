using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// The prepared workspace and diagnostics returned by a session operation.
/// </summary>
public sealed record SessionReply
{
    /// <summary>
    /// The changed-tail metadata used only for acknowledged host notifications, never stored in a session file.
    /// </summary>
    public SessionCheckpointRevision? CheckpointDelta { get; init; }

    /// <summary>
    /// The captured or reconstructed document.
    /// </summary>
    public SessionDocument Document { get; init; } = new();

    /// <summary>
    /// The original saved draft used to preserve editing through frontend startup notification delivery.
    /// </summary>
    [JsonIgnore]
    public SessionEditor? StartupEditor { get; init; }

    /// <summary>
    /// The associated session file path or download name.
    /// </summary>
    public string? Path { get; init; } = null;

    /// <summary>
    /// Whether persisted content differs from the last completed save.
    /// </summary>
    public bool Dirty { get; init; } = false;

    /// <summary>
    /// Dependency and reconstruction findings that preserve the source.
    /// </summary>
    public string[] Diagnostics { get; init; } = [];

    /// <summary>
    /// The transcript and engine status after the operation.
    /// </summary>
    public HandleReply Reply { get; init; } = new(true, false, [], SessionStatus.Initial);

    /// <summary>
    /// The prompt number awaiting an execution acknowledgement, for recovery after an interrupted runtime.
    /// </summary>
    public int? PendingSubmission { get; init; }

    /// <summary>
    /// Original source at the acknowledged execution boundary.
    /// </summary>
    public string[] PendingSource { get; init; } = [];

    /// <summary>
    /// Argument, local, and type argument declarations needed to recall an interrupted cell without its runtime values.
    /// </summary>
    public string[] PendingInputs { get; init; } = [];

    /// <summary>
    /// The conventional process exit code for a host operation that could not read its requested input.
    /// </summary>
    public int? FailureExitCode { get; init; }

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
