namespace IlRepl.Protocol;

/// <summary>
/// Distinguishes an invalid body from unfinished analysis and verification concerns.
/// </summary>
public enum AnalysisDiagnosticKind
{
    /// <summary>
    /// More source is needed before the instruction or body can be checked.
    /// </summary>
    Incomplete,

    /// <summary>
    /// The available information does not determine the instruction's effect.
    /// </summary>
    Unknown,

    /// <summary>
    /// An established path violates a correctness requirement.
    /// </summary>
    Error,

    /// <summary>
    /// The instruction has a verification concern that does not prevent execution.
    /// </summary>
    Unverifiable,
}
