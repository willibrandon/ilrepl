using PolyType;
using StreamJsonRpc;

namespace IlRepl.Protocol;

/// <summary>
/// Defines the source-generated RPC contract between the frontend and its execution host.
/// </summary>
/// <remarks>
/// The JSON-RPC contract between the front-end and the host. The host serves it; the front-end
/// talks to a source-generated proxy.
/// </remarks>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IReplHost
{
    /// <summary>
    /// Handles frontend-retained source while preserving checkpoints at execution, commit, cancellation, and failure boundaries.
    /// </summary>
    /// <param name="line">The instruction still retained by the frontend.</param>
    /// <param name="location">Its location in the submitting document.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The transcript and new status.</returns>
    Task<HandleReply> HandleRetainedSourceAsync(string line, AnalysisLocation location, CancellationToken cancellationToken);

    /// <summary>
    /// Handles a run of frontend-retained instructions in one operation, ending at the first line that is not plainly accepted.
    /// </summary>
    /// <param name="lines">The retained instructions inside an open method, in order.</param>
    /// <param name="locations">Their locations in the submitting document.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One reply for each line handled, which can be fewer than were sent.</returns>
    Task<HandleReply[]> HandleRetainedSourceRunAsync(string[] lines, AnalysisLocation[] locations, CancellationToken cancellationToken);

    /// <summary>
    /// Requests interruption without waiting for the execution gate.
    /// </summary>
    /// <param name="identity">The operation the frontend intends to interrupt.</param>
    /// <param name="cancellationToken">Cancels delivery of the control request.</param>
    /// <returns>Whether the matching operation was still running.</returns>
    Task<bool> InterruptAsync(string identity, CancellationToken cancellationToken);

    /// <summary>
    /// Inspects a prepared implementation in an isolated native compilation worker.
    /// </summary>
    /// <param name="identity">The one-use native inspection ticket.</param>
    /// <param name="cancellationToken">Cancels the isolated worker.</param>
    /// <returns>The native report and transcript.</returns>
    Task<HandleReply> InspectNativeAsync(string identity, CancellationToken cancellationToken);

    /// <summary>
    /// Captures, reconstructs, saves, or explicitly executes a typed workspace operation.
    /// </summary>
    /// <param name="request">The operation and matching editor snapshot.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The source workspace and resulting engine reply.</returns>
    Task<SessionReply> SessionAsync(SessionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the completion catalog and the initial session status.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The hello.</returns>
    Task<HostHello> HelloAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Waits for a searchable assembly load without blocking input or completion requests.
    /// </summary>
    /// <param name="version">The last observed version.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The changed version.</returns>
    Task<long> WaitForAssembliesAsync(long version, CancellationToken cancellationToken);

    /// <summary>
    /// Completes an operand against a read-only snapshot of the session and unsent document.
    /// </summary>
    /// <param name="request">The document, caret and continuation state.</param>
    /// <param name="cancellationToken">Cancels queued and active completion work.</param>
    /// <returns>The confirmed candidate page.</returns>
    Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Analyzes the unsent document against an independent session snapshot.
    /// </summary>
    /// <param name="request">The document and caret.</param>
    /// <param name="cancellationToken">Cancels queued and active analysis.</param>
    /// <returns>The source diagnostics and caret stack.</returns>
    Task<AnalysisReply> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Handles one line: an instruction, a directive, a command, or an empty line that runs the cell.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The transcript lines produced and the new status.</returns>
    Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the prepared comparison in fresh host processes after its starting state is displayed.
    /// </summary>
    /// <param name="identity">The one-use comparison ticket.</param>
    /// <param name="cancellationToken">Terminates comparison workers while preserving the live host.</param>
    /// <returns>The observations, outcome, and current session status.</returns>
    Task<HandleReply> CompareAsync(string identity, CancellationToken cancellationToken);

    /// <summary>
    /// Handles a submitted line while retaining its identity in the editor document.
    /// </summary>
    /// <param name="line">The submitted text.</param>
    /// <param name="location">Its location in the submitting document.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The reply, including diagnostics on earlier source lines.</returns>
    Task<HandleReply> HandleSourceAsync(string line, AnalysisLocation location, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws input since a mark when no run, commit or destructive command has crossed that boundary.
    /// </summary>
    /// <param name="mark">The mark to return to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was withdrawn, or why nothing could be, and the new status.</returns>
    Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken);
}
