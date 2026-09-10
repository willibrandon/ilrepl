using System.Text;
using IlRepl.Protocol;
using IlRepl.Repl;
using StreamJsonRpc;

namespace IlRepl.Host;

/// <summary>
/// Serves a <see cref="ReplCore"/> over JSON-RPC. The process console is detached from the
/// protocol streams so cells can print without corrupting the channel.
/// </summary>
public sealed class HostServer : IReplHost, IAsyncDisposable
{
    private readonly InProcessEngine _engine;

    /// <summary>
    /// Initializes a server for the given REPL.
    /// </summary>
    /// <param name="core">The REPL to serve.</param>
    public HostServer(ReplCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _engine = new InProcessEngine(core);
    }

    /// <inheritdoc />
    public Task<HostHello> HelloAsync(CancellationToken cancellationToken) => _engine.HelloAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<long> WaitForAssembliesAsync(long version, CancellationToken cancellationToken) =>
        _engine.WaitForAssembliesAsync(version, cancellationToken);

    /// <inheritdoc/>
    public Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken) =>
        _engine.CompleteAsync(request, cancellationToken);

    /// <inheritdoc/>
    public Task<AnalysisReply> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken) =>
        _engine.AnalyzeAsync(request, cancellationToken);

    /// <inheritdoc/>
    public Task<HandleReply> HandleSourceAsync(string line, AnalysisLocation location, CancellationToken cancellationToken) =>
        _engine.HandleSourceAsync(line, location, cancellationToken);

    /// <inheritdoc />
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        return _engine.HandleAsync(line, cancellationToken);
    }

    /// <inheritdoc />
    public Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return _engine.RollbackAsync(mark, cancellationToken);
    }

    /// <summary>
    /// Handles one line and returns the transcript lines it produced.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The reply.</returns>
    public HandleReply Handle(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return _engine.HandleAsync(line, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Withdraws the lines accepted since a mark and returns the transcript lines that produced.
    /// </summary>
    /// <param name="mark">The mark to return to.</param>
    /// <returns>The reply.</returns>
    public HandleReply Rollback(SessionMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return _engine.RollbackAsync(mark, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _engine.DisposeAsync();

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
        await using var server = new HostServer(new ReplCore());
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IReplHost>(), server, null);
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
