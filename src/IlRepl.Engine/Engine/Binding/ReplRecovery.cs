namespace IlRepl.Engine.Binding;

/// <summary>
/// Classifies recoverable input failures consistently for the live handler and speculative replay.
/// </summary>
internal static class ReplRecovery
{
    /// <summary>
    /// Distinguishes recoverable line errors from cancellation and process-fatal failures.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns>Whether a line checkpoint may be restored and editing continued.</returns>
    public static bool IsRecoverable(Exception exception) => exception is not
        (OperationCanceledException or OutOfMemoryException or StackOverflowException or AccessViolationException or CellException);
}
