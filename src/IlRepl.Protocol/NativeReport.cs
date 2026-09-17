namespace IlRepl.Protocol;

/// <summary>
/// An inspection side including runtime provenance and available compilation evidence.
/// </summary>
public sealed record NativeReport
{
    /// <summary>
    /// Whether this report describes the host's capabilities rather than a user-selected body.
    /// </summary>
    public bool IsCapability { get; init; } = false;

    /// <summary>
    /// Whether presentation retains original addresses, instruction encodings, and listing headers.
    /// </summary>
    public bool Raw { get; init; } = false;

    /// <summary>
    /// Whether the requested inspection completed.
    /// </summary>
    public string Outcome { get; init; } = "incomplete";

    /// <summary>
    /// The actionable failure or limitation.
    /// </summary>
    public string? Detail { get; init; } = null;

    /// <summary>
    /// The selected target.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// The captured implementation fingerprint.
    /// </summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>
    /// The selected closed implementation metadata, separate from invocation wrappers.
    /// </summary>
    public NativeMethodIdentity? Implementation { get; init; } = null;

    /// <summary>
    /// The selected implementation module's original version identity.
    /// </summary>
    public Guid ModuleVersionId { get; init; } = Guid.Empty;

    /// <summary>
    /// The worker platform ABI identity.
    /// </summary>
    public string RuntimeIdentifier { get; init; } = "";

    /// <summary>
    /// The request's explicit body execution and module initialization authorization.
    /// </summary>
    public string Authorization { get; init; } = "";

    /// <summary>
    /// The exact runtime identity.
    /// </summary>
    public string Runtime { get; init; } = "";

    /// <summary>
    /// The JIT binary identity.
    /// </summary>
    public string Jit { get; init; } = "";

    /// <summary>
    /// The worker architecture.
    /// </summary>
    public string Architecture { get; init; } = "";

    /// <summary>
    /// The worker OS and ABI description.
    /// </summary>
    public string OperatingSystem { get; init; } = "";

    /// <summary>
    /// The effective hardware intrinsic support.
    /// </summary>
    public string[] InstructionSets { get; init; } = [];

    /// <summary>
    /// The effective code-generation configuration.
    /// </summary>
    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the selected implementation is collectible.
    /// </summary>
    public bool Collectible { get; init; } = false;

    /// <summary>
    /// The workload invocations actually performed.
    /// </summary>
    public int Invocations { get; init; } = 0;

    /// <summary>
    /// The captured user output.
    /// </summary>
    public string StandardOutput { get; init; } = "";

    /// <summary>
    /// The captured user error output.
    /// </summary>
    public string StandardError { get; init; } = "";

    /// <summary>
    /// The attributed target compilations.
    /// </summary>
    public NativeCompilation[] Compilations { get; init; } = [];

    /// <summary>
    /// Selected-signature output retained without claiming complete runtime attribution.
    /// </summary>
    public string[] UnattributedListings { get; init; } = [];

    /// <summary>
    /// The address evidence used for normalization.
    /// </summary>
    public NativeAddressFact[] Addresses { get; init; } = [];

    /// <summary>
    /// Original IL numeric literals that must never be erased as address noise.
    /// </summary>
    public ulong[] Constants { get; init; } = [];

    /// <summary>
    /// The operands that could not be symbolized safely.
    /// </summary>
    public string[] NormalizationProblems { get; init; } = [];

    /// <summary>
    /// The separately identified implementation and invocation wrappers.
    /// </summary>
    public string[] Roles { get; init; } = [];
}
