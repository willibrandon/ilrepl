using IlRepl.Protocol;
using StreamJsonRpc;

namespace IlRepl.Processes;

/// <summary>
/// Runs the private process-lifetime service without participating in host execution transport.
/// </summary>
internal static class LifetimeSupervisorProgram
{
    /// <summary>
    /// Connects to the frontend lifeline and owns acknowledged groups until that connection closes.
    /// </summary>
    /// <param name="args">The private mode, socket path, and supervisor epoch.</param>
    /// <returns>The private process exit code.</returns>
    internal static async Task<int> RunAsync(string[] args)
    {
        if (OperatingSystem.IsWindows() || args.Length != 5
            || !long.TryParse(args[2], out var epoch) || !int.TryParse(args[3], out var frontendId)
            || !long.TryParse(args[4], out var frontendStart)) return 64;
        using var measurements = new ProcessMeasurements("supervisor");
        Console.CancelKeyPress += (_, eventArgs) => eventArgs.Cancel = true;
        var secret = Environment.GetEnvironmentVariable("ILREPL_SUPERVISOR_HANDSHAKE")
            ?? throw new InvalidOperationException("the supervisor has no launch identity");
        Environment.SetEnvironmentVariable("ILREPL_SUPERVISOR_HANDSHAKE", null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = await LocalSocketListener.ConnectAsync(args[1], secret, timeout.Token).ConfigureAwait(false);
        await using var service = new LifetimeSupervisorService(epoch);
        using var rpc = new JsonRpc(RpcTransport.CreateHandler(connection, connection));
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<ISupervisorService>(), service, null);
        rpc.StartListening();
        try { await rpc.Completion.ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
        service.PreserveOnDispose = OwnedProcessGroup.IsRunning(new OwnedProcessScope("frontend", frontendId, frontendStart, null));
        return 0;
    }
}
