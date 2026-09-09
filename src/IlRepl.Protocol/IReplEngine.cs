
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
    /// The words the tokenizer colours with: the engine's opcodes, directives, commands, keywords, and primitives.
    /// </summary>
    CilVocabulary Vocabulary { get; }

    /// <summary>
    /// The session status after the last handled line.
    /// </summary>
    SessionStatus Status { get; }

    /// <summary>
    /// The latest observed version of the engine's searchable loaded assemblies.
    /// </summary>
    long AssemblyVersion { get; }

    /// <summary>
    /// Waits for searchable assemblies to change, including loads outside submitted input.
    /// </summary>
    /// <param name="version">The last observed version.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The changed version, also published through <see cref="AssemblyVersion"/>.</returns>
    Task<long> WaitForAssembliesAsync(long version, CancellationToken cancellationToken);

    /// <summary>
    /// Completes an operand against the session and unsent document without submitting any lines.
    /// </summary>
    /// <param name="request">The document, caret and continuation state.</param>
    /// <param name="cancellationToken">Cancels queued and active completion work.</param>
    /// <returns>A page tied to the captured document and binding context.</returns>
    Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Handles one line and returns the transcript lines it produced.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The host's reply.</returns>
    Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws input since a mark when no run, commit or destructive command has crossed that boundary.
    /// </summary>
    /// <param name="mark">The mark to return to.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The host's reply.</returns>
    Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken);
}
