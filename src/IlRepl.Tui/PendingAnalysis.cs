namespace IlRepl.Tui;

/// <summary>
/// Owns an analysis cancellation source until its worker has settled.
/// </summary>
internal sealed record PendingAnalysis(AnalysisRequestKey Key, CancellationTokenSource Cancellation, Task Task);
