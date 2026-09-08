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
    /// Handles one line: an instruction, a directive, a command, or an empty line that runs the cell.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The transcript lines produced and the new status.</returns>
    Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws the lines accepted since a mark was taken, when nothing has run, committed, or
    /// been discarded since.
    /// </summary>
    /// <param name="mark">The mark to return to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was withdrawn, or why nothing could be, and the new status.</returns>
    Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken);
}
