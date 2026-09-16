using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// Identifies an immutable method definition and the generic instantiation selected by the author.
/// </summary>
public sealed record SessionMethodIdentity
{
    /// <summary>
    /// The complete assembly identity owning the original method.
    /// </summary>
    public string Assembly { get; set; } = "";

    /// <summary>
    /// The module identity used to reject a different binary with matching assembly metadata.
    /// </summary>
    public string Module { get; set; } = "";

    /// <summary>
    /// The metadata token of the uninstantiated method definition.
    /// </summary>
    public int Token { get; set; }

    /// <summary>
    /// Closed declaring-type arguments by assembly-qualified name, or null for an unchanged generic parameter.
    /// </summary>
    public string?[] TypeArguments { get; set; } = [];

    /// <summary>
    /// Closed method arguments by assembly-qualified name, or null for an unchanged generic parameter.
    /// </summary>
    public string?[] MethodArguments { get; set; } = [];

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
