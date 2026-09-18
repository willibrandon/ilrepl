using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// Records an explicitly interrupted execution attempt without claiming that its runtime side effects were restored.
/// </summary>
public sealed record SessionInterruption
{
    /// <summary>
    /// The stable identity of this interrupted attempt.
    /// </summary>
    public string Identity { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The positive prompt number associated with the beginning of the interrupted attempt.
    /// </summary>
    public int Number { get; set; } = 1;

    /// <summary>
    /// The original pending submission retained as physical source lines.
    /// </summary>
    public string[] Source { get; set; } = [];

    /// <summary>
    /// The operating system exit status, when the host exited before its runtime could be recovered.
    /// </summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// The bounded host diagnostic output retained with the interrupted attempt.
    /// </summary>
    public string? StandardError { get; set; }

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
