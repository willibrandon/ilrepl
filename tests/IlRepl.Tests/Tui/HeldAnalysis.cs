using IlRepl.Protocol;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Holds one analysis reply so tests can complete requests in a different order from arrival.
/// </summary>
internal sealed record HeldAnalysis(AnalysisRequest Request, CancellationToken Cancellation)
{
    /// <summary>
    /// The independently controlled engine response.
    /// </summary>
    public TaskCompletionSource<AnalysisReply> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
