using PolyType;
using StreamJsonRpc;

namespace IlRepl.Protocol;

/// <summary>
/// The JSON-RPC contract between the front-end and the host. The host serves it; the front-end
/// talks to a source-generated proxy.
/// </summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IReplHost
{
    /// <summary>
    /// Returns the completion catalog and the initial session status.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The hello.</returns>
    Task<HostHello> HelloAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Completes an operand against a read-only snapshot of the session and unsent document.
    /// </summary>
    /// <param name="request">The document, caret and continuation state.</param>
    /// <param name="cancellationToken">Cancels queued and active completion work.</param>
    /// <returns>The confirmed candidate page.</returns>
    Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Handles one line: an instruction, a directive, a command, or an empty line that runs the cell.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The transcript lines produced and the new status.</returns>
    Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws input since a mark when no run, commit or destructive command has crossed that boundary.
    /// </summary>
    /// <param name="mark">The mark to return to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was withdrawn, or why nothing could be, and the new status.</returns>
    Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken);
}
