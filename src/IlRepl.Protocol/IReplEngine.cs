
namespace IlRepl.Protocol;

/// <summary>
/// Defines the engine operations shared by terminal, batch, and browser frontends.
/// </summary>
/// <remarks>
/// What the front-end needs from a REPL engine: a completion catalog, the session status, and
/// a way to hand it lines.
/// </remarks>
public interface IReplEngine : IAsyncDisposable
{
    /// <summary>
    /// Captures, reconstructs, or explicitly executes a typed session workspace operation.
    /// </summary>
    /// <param name="request">The operation and matching editor snapshot.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The resulting source workspace and engine reply.</returns>
    Task<SessionReply> SessionAsync(SessionRequest request, CancellationToken cancellationToken) =>
        Task.FromException<SessionReply>(new NotSupportedException("this engine has no session document support"));

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
    /// Analyzes the unsent document without executing commands or creating runtime definitions.
    /// </summary>
    /// <param name="request">The document and caret.</param>
    /// <param name="cancellationToken">Cancels queued and active analysis.</param>
    /// <returns>The source diagnostics and caret stack.</returns>
    Task<AnalysisReply> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Handles one line and returns the transcript lines it produced.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The host's reply.</returns>
    Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a prepared comparison in isolated workers after its starting-state reply has been displayed.
    /// </summary>
    /// <param name="identity">The one-use ticket returned by .compare.</param>
    /// <param name="cancellationToken">Terminates comparison workers without cancelling the live session.</param>
    /// <returns>The typed comparison observations and transcript.</returns>
    Task<HandleReply> CompareAsync(string identity, CancellationToken cancellationToken) =>
        Task.FromException<HandleReply>(new NotSupportedException("this engine has no isolated comparison runner"));

    /// <summary>
    /// Handles a submitted line while retaining its identity in the editor document.
    /// </summary>
    /// <param name="line">The submitted text.</param>
    /// <param name="location">Its location in the submitting document.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The reply, including diagnostics on earlier source lines.</returns>
    Task<HandleReply> HandleSourceAsync(string line, AnalysisLocation location, CancellationToken cancellationToken) =>
        HandleAsync(line, cancellationToken);

    /// <summary>
    /// Withdraws input since a mark when no run, commit or destructive command has crossed that boundary.
    /// </summary>
    /// <param name="mark">The mark to return to.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The host's reply.</returns>
    Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken);
}
