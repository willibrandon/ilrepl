namespace IlRepl.Protocol;

/// <summary>
/// An inspection request with captured inputs shared by isolated workers.
/// </summary>
public sealed record NativePackage
{
    /// <summary>
    /// The parsed command options.
    /// </summary>
    public NativeOptions Options { get; init; } = new();

    /// <summary>
    /// The primary captured target.
    /// </summary>
    public NativeTarget Left { get; init; } = new();

    /// <summary>
    /// The optional comparison target.
    /// </summary>
    public NativeTarget? Right { get; init; } = null;

    /// <summary>
    /// The inherited process environment.
    /// </summary>
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The immutable working directory fixtures.
    /// </summary>
    public ComparisonFile[] Files { get; init; } = [];

    /// <summary>
    /// The captured culture name.
    /// </summary>
    public string Culture { get; init; } = "";

    /// <summary>
    /// The captured UI culture name.
    /// </summary>
    public string UICulture { get; init; } = "";
}
