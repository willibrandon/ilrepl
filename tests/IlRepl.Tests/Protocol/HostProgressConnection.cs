using System.Collections.Concurrent;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Repl;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Receives real source acknowledgements and execution transitions from a real engine over duplex JSON-RPC streams.
/// </summary>
internal sealed class HostProgressConnection : IReplClient, IAsyncDisposable
{
    private readonly JsonRpc _server;
    private readonly JsonRpc _client;
    private readonly HostServer _host;
    private readonly SessionCheckpointStore _checkpoints = new();

    /// <summary>
    /// Connects an actual engine and generated frontend proxy to this recording protocol endpoint.
    /// </summary>
    /// <param name="core">The real core executed by the host.</param>
    internal HostProgressConnection(ReplCore core)
    {
        var (clientPipe, serverPipe) = FullDuplexStream.CreatePair();
        _server = new JsonRpc(RpcTransport.CreateHandler(serverPipe, serverPipe));
        _client = new JsonRpc(RpcTransport.CreateHandler(clientPipe, clientPipe));
        _host = new HostServer(core, _server.Attach<IReplClient>());
        _server.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IReplHost>(), _host, null);
        _client.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IReplClient>(), this, null);
        Proxy = _client.Attach<IReplHost>();
        _server.StartListening();
        _client.StartListening();
    }

    /// <summary>
    /// The generated RPC proxy that invokes the real host.
    /// </summary>
    internal IReplHost Proxy { get; }

    /// <summary>
    /// Actual progress messages delivered independently of source replies.
    /// </summary>
    internal ConcurrentQueue<ExecutionProgress> Progress { get; } = new();

    /// <summary>
    /// Actual source revisions reconstructed before acknowledgement reaches the host.
    /// </summary>
    internal ConcurrentQueue<SessionReply> Checkpoints { get; } = new();

    /// <summary>
    /// Console chunks published by actual user execution.
    /// </summary>
    internal ConcurrentQueue<ExecutionOutput> Output { get; } = new();

    /// <summary>
    /// An optional acknowledgement barrier applied after the received progress is recorded.
    /// </summary>
    internal Func<ExecutionProgress, Task>? AcknowledgeProgress { get; set; }

    /// <summary>
    /// An optional acknowledgement barrier applied after the source revision is reconstructed and recorded.
    /// </summary>
    internal Func<SessionReply, Task>? AcknowledgeCheckpoint { get; set; }

    /// <inheritdoc />
    public async Task ExecutionChangedAsync(ExecutionProgress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Progress.Enqueue(progress);
        if (AcknowledgeProgress is { } acknowledge) await acknowledge(progress).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CheckpointAsync(SessionReply checkpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_checkpoints.Apply(checkpoint) is not { } accepted) return;
        Checkpoints.Enqueue(accepted);
        if (AcknowledgeCheckpoint is { } acknowledge) await acknowledge(accepted).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task OutputAsync(ExecutionOutput output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Output.Enqueue(output);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RegisterProcessAsync(OwnedProcessScope scope, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("this protocol endpoint does not launch process workers"));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _server.Dispose();
        await _host.DisposeAsync().ConfigureAwait(false);
    }
}
