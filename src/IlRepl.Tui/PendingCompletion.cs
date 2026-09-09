namespace IlRepl.Tui;

/// <summary>
/// Owns a request and its cancellation source until the task settles, including after it is superseded.
/// </summary>
/// <param name="Key">The local query identity.</param>
/// <param name="Cancellation">The request's cancellation source.</param>
/// <param name="Task">The task whose settlement releases the request.</param>
internal sealed record PendingCompletion(CompletionRequestKey Key, CancellationTokenSource Cancellation, Task Task);
