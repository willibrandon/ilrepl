using System.Text;
using IlRepl.Protocol;
using IlRepl.Repl;
using StreamJsonRpc;

namespace IlRepl.Host;

/// <summary>
/// Serves a <see cref="ReplCore"/> over JSON-RPC. The process console is detached from the
/// protocol streams so cells can print without corrupting the channel.
/// </summary>
public sealed class HostServer : IReplHost
{
    private readonly ReplCore _core;

    /// <summary>
    /// Initializes a server for the given REPL.
    /// </summary>
    /// <param name="core">The REPL to serve.</param>
    public HostServer(ReplCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
    }

    /// <inheritdoc />
    public Task<HostHello> HelloAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new HostHello(Completer.Catalog, _core.Status));

    /// <inheritdoc />
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        return Task.FromResult(Handle(line));
    }

    /// <summary>
    /// Handles one line and returns the transcript lines it produced.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The reply.</returns>
    public HandleReply Handle(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var result = _core.Handle(line);
        var lines = _core.Transcript.Lines.ToArray();
        _core.Transcript.Clear();
        return new HandleReply(result.Succeeded, result.QuitRequested, lines, _core.Status);
    }

    /// <summary>
    /// Serves the process's standard streams until the front-end disconnects.
    /// </summary>
    /// <param name="cancellationToken">Stops the server.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> ServeStandardStreamsAsync(CancellationToken cancellationToken)
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        Console.SetOut(TextWriter.Null);
        Console.SetIn(TextReader.Null);
        Console.OutputEncoding = new UTF8Encoding(false);

        using var rpc = new JsonRpc(RpcTransport.CreateHandler(output, input));
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IReplHost>(), new HostServer(new ReplCore()), null);
        rpc.StartListening();
        using var registration = cancellationToken.Register(rpc.Dispose);
        try
        {
            await rpc.Completion.ConfigureAwait(false);
        }
        catch (ConnectionLostException)
        {
            // The front-end went away; nothing more to do.
        }
        catch (ObjectDisposedException)
        {
            // Cancelled.
        }

        return 0;
    }
}
