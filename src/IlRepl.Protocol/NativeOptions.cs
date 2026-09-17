namespace IlRepl.Protocol;

/// <summary>
/// The parsed target, compilation profile, and explicitly authorized workload.
/// </summary>
public sealed record NativeOptions
{
    /// <summary>
    /// The primary method or cell selector.
    /// </summary>
    public string Selector { get; init; } = "";

    /// <summary>
    /// The optional comparison selector.
    /// </summary>
    public string? Against { get; init; } = null;

    /// <summary>
    /// Whether the primary selector names its edit baseline.
    /// </summary>
    public bool Original { get; init; } = false;

    /// <summary>
    /// Whether only host capabilities are requested.
    /// </summary>
    public bool Info { get; init; } = false;

    /// <summary>
    /// The requested compilation mode.
    /// </summary>
    public string Tier { get; init; } = "fullopts";

    /// <summary>
    /// Whether user assemblies use collectible loading.
    /// </summary>
    public bool Collectible { get; init; } = false;

    /// <summary>
    /// Whether tiered compilation collects dynamic profiles.
    /// </summary>
    public bool Pgo { get; init; } = true;

    /// <summary>
    /// Whether encoding details are retained.
    /// </summary>
    public bool Raw { get; init; } = false;

    /// <summary>
    /// Whether native differences fail the command.
    /// </summary>
    public bool Assert { get; init; } = false;

    /// <summary>
    /// Whether preparation may activate user module initializers.
    /// </summary>
    public bool AllowInitializers { get; init; } = false;

    /// <summary>
    /// Whether the selected workload is explicitly authorized.
    /// </summary>
    public bool Run { get; init; } = false;

    /// <summary>
    /// The explicitly supplied literal arguments.
    /// </summary>
    public string[]? Arguments { get; init; } = null;

    /// <summary>
    /// The parameterless session workload.
    /// </summary>
    public string? Scenario { get; init; } = null;

    /// <summary>
    /// The maximum number of workload invocations.
    /// </summary>
    public int Iterations { get; init; } = 1;

    /// <summary>
    /// The work deadline after worker readiness.
    /// </summary>
    public int TimeoutMilliseconds { get; init; } = 30000;

    /// <summary>
    /// The workload standard input.
    /// </summary>
    public string StandardInput { get; init; } = "";

    /// <summary>
    /// The directory copied independently before each side.
    /// </summary>
    public string? FixtureDirectory { get; init; } = null;

    /// <summary>
    /// The explicit worker environment overrides.
    /// </summary>
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);
}
