using PolyType;
using StreamJsonRpc;

namespace IlRepl.Protocol;

/// <summary>
/// Controls process lifetimes over a private connection that never carries execution RPC traffic.
/// </summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ISupervisorService
{
    /// <summary>
    /// Validates and adopts the frontend's acknowledged process registry.
    /// </summary>
    /// <param name="snapshot">The current generation and scopes.</param>
    /// <param name="cancellationToken">Cancels adoption.</param>
    /// <returns>The identities still owned after validation.</returns>
    Task<string[]> AdoptAsync(SupervisorSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a host with inherited diagnostic descriptors and closed ordinary stdin.
    /// </summary>
    /// <param name="request">The exact launch request.</param>
    /// <param name="cancellationToken">Cancels launch.</param>
    /// <returns>The launched scope, still blocked by its frontend handshake.</returns>
    Task<OwnedProcessScope> LaunchAsync(SupervisorLaunch request, CancellationToken cancellationToken);

    /// <summary>
    /// Stops a runtime scope and every registered descendant group.
    /// </summary>
    /// <param name="identity">The runtime identity.</param>
    /// <param name="epoch">The supervisor generation.</param>
    /// <param name="cancellationToken">Cancels the wait for confirmed termination.</param>
    /// <returns>Completion after the groups no longer execute.</returns>
    Task StopAsync(string identity, long epoch, CancellationToken cancellationToken);

    /// <summary>
    /// Returns an observed native exit status when this supervisor remained the process parent.
    /// </summary>
    /// <param name="identity">The process scope identity.</param>
    /// <param name="epoch">The supervisor generation.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The observed exit status, or null after adoption or before exit.</returns>
    Task<int?> ExitCodeAsync(string identity, long epoch, CancellationToken cancellationToken);
    /// <summary>
    /// Writes a boundary to the inherited diagnostic pipe after the observed child has exited.
    /// </summary>
    /// <param name="marker">The private reader acknowledgement marker.</param>
    /// <param name="epoch">The supervisor generation.</param>
    /// <param name="cancellationToken">Cancels boundary publication.</param>
    /// <returns>Completion after the marker has entered the direct diagnostic pipe.</returns>
    Task FlushDiagnosticsAsync(string marker, long epoch, CancellationToken cancellationToken);
}
