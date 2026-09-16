using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// A dependency request and its resolved identity and verified assets.
/// </summary>
public sealed record SessionReference
{
    /// <summary>
    /// The stable internal reference identity.
    /// </summary>
    public string Identity { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The origin: assembly, package, project, or baseline.
    /// </summary>
    public string Origin { get; set; } = "assembly";

    /// <summary>
    /// The assembly, package, or project requested by the user.
    /// </summary>
    public string Request { get; set; } = "";

    /// <summary>
    /// The requested NuGet version range, separate from its resolved version.
    /// </summary>
    public string? RequestedVersion { get; set; } = null;

    /// <summary>
    /// The exact resolved package or assembly version.
    /// </summary>
    public string? Version { get; set; } = null;

    /// <summary>
    /// The selected target framework.
    /// </summary>
    public string? Framework { get; set; } = null;

    /// <summary>
    /// The project build configuration.
    /// </summary>
    public string? Configuration { get; set; } = null;

    /// <summary>
    /// The SDK used to evaluate and build a project.
    /// </summary>
    public string? SdkVersion { get; set; } = null;

    /// <summary>
    /// Selected implementation, reference, native, and satellite assets.
    /// </summary>
    public SessionReferenceAsset[] Assets { get; set; } = [];

    /// <summary>
    /// The identities of dependencies in the resolved graph.
    /// </summary>
    public string[] Dependencies { get; set; } = [];

    /// <summary>
    /// Additional shared frameworks required for execution.
    /// </summary>
    public string[] Frameworks { get; set; } = [];

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
