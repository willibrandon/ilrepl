using IlRepl.Protocol;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Retains a real completed or computing response until its delivery permit is released.
/// </summary>
/// <param name="Request">The submitted document.</param>
/// <param name="Cancellation">The caller's cancellation token, deliberately independent of delivery.</param>
/// <param name="Prepared">The actual engine computation.</param>
internal sealed record HeldCompletion(CompletionRequest Request, Task<CompletionReply> Prepared, CancellationToken Cancellation)
{
    /// <summary>
    /// Allows the real response to reach its requester.
    /// </summary>
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
