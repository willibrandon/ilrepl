using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Carries a settled completion request back to the render thread without adding transcript output.
/// </summary>
/// <param name="Generation">The requester's lifetime generation.</param>
/// <param name="Key">The immutable local query key.</param>
/// <param name="Reply">The reply, or null after failure or cancellation.</param>
/// <param name="Cancelled">Whether the request was cancelled.</param>
/// <param name="Faulted">Whether the request failed.</param>
public sealed record CompletionResult(
    long Generation, CompletionRequestKey Key, CompletionReply? Reply, bool Cancelled = false, bool Faulted = false);
