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
    /// The operation whose console output was also streamed before this reply.
    /// </summary>
    public string? OutputIdentity { get; init; }

    /// <summary>
    /// The last acknowledged output chunk, used to avoid displaying streamed output twice.
    /// </summary>
    public long OutputSequence { get; init; }

    /// <summary>
    /// Reply line indexes already acknowledged as leading transcript records on the streamed output channel.
    /// </summary>
    public IReadOnlyList<int> StreamedLineIndexes { get; init; } = [];

    /// <summary>
    /// An assembly awaiting delivery through the frontend's export destination.
    /// </summary>
    public AssemblyExportResult? AssemblyExport { get; init; }

    /// <summary>
    /// The typed workspace action awaiting the frontend coordinator.
    /// </summary>
    public SessionAction? SessionAction { get; init; }

    /// <summary>
    /// The restored editor snapshot supplied by a session action.
    /// </summary>
    public SessionEditor? SessionEditor { get; init; }

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

    /// <summary>
    /// The native listing or comparison produced by an isolated worker.
    /// </summary>
    public NativeReply? Native { get; init; }

    /// <summary>
    /// The native request ready to run after its configuration has been displayed.
    /// </summary>
    public NativeTicket? PendingNative { get; init; }
}
