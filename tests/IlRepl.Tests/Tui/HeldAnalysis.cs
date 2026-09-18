using IlRepl.Protocol;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Retains a real analyzed document independently of cancellation to exercise stale delivery.
/// </summary>
/// <param name="Request">The document submitted to analysis.</param>
/// <param name="Cancellation">The caller's cancellation token.</param>
/// <param name="Prepared">The actual engine computation.</param>
internal sealed record HeldAnalysis(AnalysisRequest Request, Task<AnalysisReply> Prepared, CancellationToken Cancellation)
{
    /// <summary>
    /// Allows the actual analysis response to reach its requester.
    /// </summary>
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
