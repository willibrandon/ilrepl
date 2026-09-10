using IlRepl.Protocol;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Retains one scripted reply independently of cancellation to exercise late host responses.
/// </summary>
/// <param name="Request">The submitted document.</param>
/// <param name="Cancellation">The request's cancellation token.</param>
internal sealed record HeldCompletion(CompletionRequest Request, CancellationToken Cancellation)
{
    /// <summary>
    /// The independently releasable response.
    /// </summary>
    public TaskCompletionSource<CompletionReply> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
