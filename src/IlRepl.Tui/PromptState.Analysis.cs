using IlRepl.Protocol;

namespace IlRepl.Tui;

public sealed partial class PromptState
{
    /// <summary>
    /// Coordinates document analysis independently of completion sites.
    /// </summary>
    public AnalysisRequester? Analyzer { get; set; }

    /// <summary>
    /// The analysis currently matching the document and caret.
    /// </summary>
    public AnalysisReply? Analysis { get; set; }

    /// <summary>
    /// Keeps the previous explanation visible while updated analysis is pending.
    /// </summary>
    public DiagnosticDisplay? PendingDiagnostic { get; set; }
}
