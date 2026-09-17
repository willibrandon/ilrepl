namespace IlRepl.Protocol;

/// <summary>
/// The native inspection or comparison outcome.
/// </summary>
public sealed record NativeReply
{
    /// <summary>
    /// Complete, equal, different, or indeterminate.
    /// </summary>
    public string Outcome { get; init; } = "incomplete";

    /// <summary>
    /// The primary report.
    /// </summary>
    public NativeReport Left { get; init; } = new();

    /// <summary>
    /// The optional comparison report.
    /// </summary>
    public NativeReport? Right { get; init; } = null;

    /// <summary>
    /// The unified native instruction differences.
    /// </summary>
    public string[] Difference { get; init; } = [];
}
