using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// Retains the immutable identity and original source of an edit independently of its later revisions.
/// </summary>
public sealed record SessionEditSnapshot
{
    /// <summary>
    /// The stable name chosen for the edit.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The method reference used when the original was captured.
    /// </summary>
    public string Reference { get; set; } = "";

    /// <summary>
    /// The immutable original's fingerprint, including its original module identity.
    /// </summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>
    /// The original editable source before any revision was committed.
    /// </summary>
    public string[] Source { get; set; } = [];

    /// <summary>
    /// Whether the command began an outer edit block.
    /// </summary>
    public bool OpensBlock { get; set; }

    /// <summary>
    /// The baseline asset reference retained even by a non-embedded session save.
    /// </summary>
    public string? BaselineReference { get; set; }

    /// <summary>
    /// The original method in the frozen dependency graph, including any closed generic arguments.
    /// </summary>
    public SessionMethodIdentity? Original { get; set; }

    /// <summary>
    /// The immutable implementations behind source-time session method names.
    /// </summary>
    public Dictionary<string, SessionMethodIdentity> PinnedMethods { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The original session method headers used to reconstruct exact call signatures.
    /// </summary>
    public Dictionary<string, string> SignatureHeaders { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Source-time type names mapped to their immutable assembly-qualified identities.
    /// </summary>
    public Dictionary<string, string> TypeAliases { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Source-time edit aliases mapped to their immutable callable methods.
    /// </summary>
    public Dictionary<string, SessionMethodIdentity> MethodAliases { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
