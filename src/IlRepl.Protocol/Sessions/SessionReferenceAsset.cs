using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// An asset locator pinned to the bytes used by the experiment.
/// </summary>
public sealed record SessionReferenceAsset
{
    /// <summary>
    /// The assembly identity or native asset name.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The SHA-256 hash of the asset bytes.
    /// </summary>
    public string Hash { get; set; } = "";

    /// <summary>
    /// The optional session-relative file locator, resolved to a local absolute path by the desktop host.
    /// </summary>
    public string? Path { get; set; } = null;

    /// <summary>
    /// The optional package asset locator relative to the root of the resolved package.
    /// </summary>
    public string? PackagePath { get; set; } = null;

    /// <summary>
    /// The managed module version identifier.
    /// </summary>
    public string? Mvid { get; set; } = null;

    /// <summary>
    /// The asset kind: managed, reference, native, or satellite.
    /// </summary>
    public string Kind { get; set; } = "managed";

    /// <summary>
    /// The runtime identifier required by this asset.
    /// </summary>
    public string? Rid { get; set; } = null;

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
