
namespace IlRepl.Protocol;

/// <summary>
/// What the front-end needs from a REPL engine: a completion catalog, the session status, and
/// a way to hand it lines.
/// </summary>
public interface IReplEngine : IAsyncDisposable
{
    /// <summary>
    /// Every opcode and command the completer offers.
    /// </summary>
    IReadOnlyList<CompletionItem> Catalog { get; }

    /// <summary>
    /// The session status after the last handled line.
    /// </summary>
    SessionStatus Status { get; }

    /// <summary>
    /// Handles one line and returns the transcript lines it produced.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The host's reply.</returns>
    Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws the lines accepted since a mark was taken, when nothing has run, committed, or
    /// been discarded since. The reply says what was withdrawn, or why nothing could be.
    /// </summary>
    /// <param name="mark">The mark to return to.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The host's reply.</returns>
    Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken);
}
