using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// An embedded dependency image identified by its SHA-256 content hash.
/// </summary>
public sealed record SessionAsset
{
    /// <summary>
    /// The lowercase SHA-256 hash of the decoded bytes.
    /// </summary>
    public string Hash { get; set; } = "";

    /// <summary>
    /// The exact bytes retained for portable reopening.
    /// </summary>
    public byte[] Image { get; set; } = [];

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
