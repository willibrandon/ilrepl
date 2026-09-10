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
}
