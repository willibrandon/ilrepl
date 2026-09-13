namespace IlRepl.Protocol;

/// <summary>
/// The host's answer to one handled line.
/// </summary>
/// <param name="Succeeded">False when the line produced an error.</param>
/// <param name="Quit">True when the line asked to leave.</param>
/// <param name="Lines">The transcript lines the line produced, in order.</param>
/// <param name="Status">The session status after the line.</param>
public sealed record HandleReply(bool Succeeded, bool Quit, IReadOnlyList<TranscriptLine> Lines, SessionStatus Status)
{
    /// <summary>
    /// Source findings that explain a refused line, including earlier instructions affected by it.
    /// </summary>
    public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// A requested method draft to hydrate in the caller's editor.
    /// </summary>
    public EditDocument? EditDocument { get; init; }

    /// <summary>
    /// Structured instruction, metadata, and stack differences produced by .diff.
    /// </summary>
    public EditDiff? Diff { get; init; }

    /// <summary>
    /// The observations from an isolated original-versus-copy execution.
    /// </summary>
    public ComparisonReply? Comparison { get; init; }

    /// <summary>
    /// A comparison ready to execute after the caller displays its explicit starting conditions.
    /// </summary>
    public ComparisonTicket? PendingComparison { get; init; }
}
