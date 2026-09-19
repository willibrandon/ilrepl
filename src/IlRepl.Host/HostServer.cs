using System.Text;
using IlRepl.Protocol;
using IlRepl.Repl;
using StreamJsonRpc;

namespace IlRepl.Host;

/// <summary>
/// Exposes the execution engine and terminal workspace operations through the host RPC connection.
/// </summary>
/// <remarks>
/// Serves a <see cref="ReplCore"/> over JSON-RPC. The process console is detached from the
/// protocol streams so cells can print without corrupting the channel.
/// </remarks>
public sealed partial class HostServer : IReplHost, IAsyncDisposable
{
    /// <inheritdoc />
    public async Task<SessionReply> SessionAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _engine.SessionAsync(request, cancellationToken).ConfigureAwait(false);
            var response = reply with { Reply = WithStreamedOutput(reply.Reply) };
            return _client is not null && request.CheckpointDelivery is { } identity && reply.CheckpointDelivery == identity
                ? response with { Document = new SessionDocument() }
                : response with { CheckpointDelivery = null };
        }
        catch (Exception exception) when (request.Action.Operation == SessionOperation.Open
            && exception is not InvalidDataException && exception is IOException or UnauthorizedAccessException)
        {
            return new SessionReply
            {
                FailureExitCode = 2,
                Reply = new HandleReply(false, false,
                    [TranscriptLine.Of(LineKind.Error, exception.Message, SpanStyle.Error)], _engine.Status),
            };
        }
    }

    private readonly InProcessEngine _engine;
    private readonly ReplCore _core;
    private readonly IReplClient? _client;

    /// <summary>
    /// Initializes a server for the given REPL.
    /// </summary>
    /// <param name="core">The REPL to serve.</param>
    /// <param name="client">The connected frontend that acknowledges source and execution progress.</param>
    public HostServer(ReplCore core, IReplClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
        _client = client;
        _engine = new InProcessEngine(core,
            (package, token) => ProcessComparisonRunner.RunAsync(package, RegisterProcessAsync, token),
            (package, token) => ProcessNativeRunner.RunAsync(package, RegisterProcessAsync, token));
        _engine.SessionTooling = new HostSessionService(_engine).ExecuteAsync;
        _engine.WorkspaceCheckpoint = checkpoint => PublishWorkspaceAsync(checkpoint, CancellationToken.None).GetAwaiter().GetResult();
        core.OutputReceived = PublishOutput;
        core.Transcript.LineAdded += RetainOutputPrefix;
        _engine.ProgressPublisher = PublishProgressAsync;
        core.BeforeExecution = () => PublishCheckpoint(executing: true);
        core.SourceCheckpoint = () => PublishCheckpoint(executing: false);
    }

    /// <inheritdoc />
    public Task<bool> InterruptAsync(string identity, CancellationToken cancellationToken) =>
        _engine.InterruptAsync(identity, cancellationToken);

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
        HandleWithCompletionAsync(() => _engine.HandleSourceAsync(line, location, cancellationToken));

    /// <inheritdoc />
    public Task<HandleReply> HandleRetainedSourceAsync(string line, AnalysisLocation location, CancellationToken cancellationToken) =>
        HandleWithCompletionAsync(() => _engine.HandleRetainedSourceAsync(line, location, cancellationToken));

    /// <inheritdoc />
    public Task<HandleReply[]> HandleRetainedSourceRunAsync(
        string[] lines,
        AnalysisLocation[] locations,
        CancellationToken cancellationToken) =>
        HandleRunWithCompletionAsync(() => _engine.HandleRetainedSourceRunAsync(lines, locations, cancellationToken));

    /// <inheritdoc/>
    public Task<HandleReply> CompareAsync(string identity, CancellationToken cancellationToken) =>
        _engine.CompareAsync(identity, cancellationToken);

    /// <inheritdoc/>
    public Task<HandleReply> InspectNativeAsync(string identity, CancellationToken cancellationToken) =>
        _engine.InspectNativeAsync(identity, cancellationToken);

    /// <inheritdoc />
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        return HandleWithCompletionAsync(() => _engine.HandleAsync(line, cancellationToken));
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
    /// Serves the frontend's private socket without using process standard streams for RPC.
    /// </summary>
    /// <param name="path">The frontend-owned endpoint.</param>
    /// <param name="secret">The launch-specific bootstrap secret.</param>
    /// <param name="leaving">What the caller does on its way out, run here when the host leaves without returning.</param>
    /// <param name="cancellationToken">Stops the server.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> ServeSocketAsync(string path, string secret, Action leaving, CancellationToken cancellationToken)
    {
        var frontendOwner = ReadFrontendOwner();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(30));
        await using var connection = await LocalSocketListener.ConnectAsync(path, secret, startup.Token).ConfigureAwait(false);
        Console.SetOut(TextWriter.Null);
        Console.SetIn(TextReader.Null);
        Console.OutputEncoding = new UTF8Encoding(false);

        using var rpc = new JsonRpc(RpcTransport.CreateHandler(connection, connection));
        var server = new HostServer(new ReplCore(), rpc.Attach<IReplClient>());
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IReplHost>(), server, null);
        rpc.StartListening();
        using var registration = cancellationToken.Register(rpc.Dispose);
        try
        {
            await rpc.Completion.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ConnectionLostException or ObjectDisposedException)
        {
            // The owning frontend closed its direct connection.
        }
        finally
        {
            // Disposal may be waiting for arbitrary user code; losing the frontend cannot leave that code running.
            var settled = false;
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    await server.StopOwnedWorkersAsync().WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                }

                await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                settled = true;
            }
            catch (TimeoutException)
            {
            }

            if (!settled || !OwnedProcessGroup.IsRunning(frontendOwner))
            {
                // This way out stops the process where it stands, so the caller never gets to unwind.
                leaving();
                if (!OperatingSystem.IsWindows())
                {
                    using var group = new OwnedProcessGroup();
                    group.Adopt(Environment.ProcessId);
                    await group.StopAsync().ConfigureAwait(false);
                }

                Environment.Exit(3);
            }
        }

        return 0;
    }
}
