namespace IlRepl.Tui;

/// <summary>
/// Owns an analysis cancellation source until its worker has settled.
/// </summary>
internal sealed record PendingAnalysis(long Id, AnalysisRequestKey Key, CancellationTokenSource Cancellation, Task Task) : IDisposable
{
    /// <summary>
    /// Releases the cancellation source once the worker has settled.
    /// </summary>
    public void Dispose() => Cancellation.Dispose();
}
