namespace IlRepl.Protocol;

/// <summary>
/// The immutable versions, inputs, and starting conditions supplied to both isolated executions.
/// </summary>
/// <param name="Name">The edit name.</param>
/// <param name="BaselineFingerprint">The immutable original identity.</param>
/// <param name="Revision">The committed revision captured.</param>
/// <param name="Original">The original-side executable snapshot.</param>
/// <param name="Edited">The edited-side executable snapshot.</param>
/// <param name="Dependencies">The captured non-framework dependency images.</param>
/// <param name="Environment">The environment variable snapshot.</param>
/// <param name="Culture">The current culture name.</param>
/// <param name="UICulture">The current UI culture name.</param>
/// <param name="StandardInput">The identical input stream supplied to both sides.</param>
/// <param name="Files">The working-directory fixtures supplied independently to each side.</param>
/// <param name="TimeoutMilliseconds">The per-side execution timeout after runtime startup.</param>
/// <param name="OutputLimit">The maximum captured character count per output stream.</param>
/// <param name="Assert">Whether any non-match must fail a batch submission.</param>
public sealed record ComparisonPackage(
    string Name,
    string BaselineFingerprint,
    int Revision,
    ComparisonImage Original,
    ComparisonImage Edited,
    IReadOnlyList<ComparisonAssembly> Dependencies,
    IReadOnlyDictionary<string, string> Environment,
    string Culture,
    string UICulture,
    string StandardInput,
    IReadOnlyList<ComparisonFile> Files,
    int TimeoutMilliseconds,
    int OutputLimit,
    bool Assert)
{
    /// <summary>
    /// The starting-state description printed before either worker executes user code.
    /// </summary>
    public string StartingState => $"fresh declarations and initializers; identical literal or scenario inputs; culture '{Culture}'; "
        + $"{Environment.Count} captured environment variables; identical stdin; separate working directories with {Files.Count} fixtures; "
        + "previously executed cells are not replayed; clocks, randomness, absolute paths, and external services remain shared conditions";
}
