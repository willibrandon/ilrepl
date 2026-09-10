using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Carries a completed request back to the render thread.
/// </summary>
internal sealed record CompletedAnalysis(AnalysisRequestKey Key, AnalysisReply? Reply);
