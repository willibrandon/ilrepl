using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using IlRepl.Protocol;
using StreamJsonRpc;

namespace IlRepl.Processes;

/// <summary>
/// Runs a framework-dependent execution host over a direct Unix domain socket on Windows, Linux, and macOS.
/// </summary>
/// <remarks>
/// Process ownership and diagnostic pipes remain independent of the direct JSON-RPC connection.
/// </remarks>
public sealed partial class HostProcessEngine : IReplEngine
{
    /// <inheritdoc />
    public async Task<SessionReply> SessionAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var reply = await InvokeMutationAsync(token => _host.SessionAsync(request, token), cancellationToken).ConfigureAwait(false);
            if (reply.FailureExitCode is { } exitCode)
            {
                throw new ReplEngineException(string.Join('\n', reply.Reply.Lines.Select(line => line.PlainText)))
                {
                    ExitCode = exitCode,
                };
            }

            Status = reply.Reply.Status;
            return reply;
        }
        catch (RemoteInvocationException exception)
        {
            throw new ReplEngineException(exception.Message, exception);
        }
        catch (ConnectionLostException exception)
        {
            await ObserveConnectionLossAsync().ConfigureAwait(false);
            throw new HostProtocolException("the host exited" + ExitDetail(), exception);
        }
    }

    private readonly Process _process;
    private readonly HostProcessLifetime _lifetime;
    private readonly OwnedProcessScope _scope;
    private readonly bool _ownsLifetime;
    private readonly TaskCompletionSource<HostExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly JsonRpc _rpc;
    private readonly IReplHost _host;
    private readonly DiagnosticTail _stderr;
    private readonly LocalSocketListener _listener;
    private readonly Stream _connection;
    private bool _disposed;
    private long _assemblyVersion;

    private HostProcessEngine(Process process, JsonRpc rpc, IReplHost host, DiagnosticTail stderr, HostHello hello,
        LocalSocketListener listener, Stream connection, HostProcessLifetime lifetime, OwnedProcessScope scope, bool ownsLifetime)
    {
        _process = process;
        _lifetime = lifetime;
        _scope = scope;
        _ownsLifetime = ownsLifetime;
        _rpc = rpc;
        _host = host;
        _stderr = stderr;
        _listener = listener;
        _connection = connection;
        Catalog = hello.Catalog;
        Vocabulary = hello.Vocabulary;
        Status = hello.Status;
        _assemblyVersion = hello.AssemblyVersion;
    }

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> Catalog { get; }

    /// <inheritdoc />
    public CilVocabulary Vocabulary { get; }

    /// <inheritdoc />
    public SessionStatus Status { get; private set; }

    /// <inheritdoc/>
    public long AssemblyVersion => Interlocked.Read(ref _assemblyVersion);

    /// <inheritdoc/>
    public async Task<AnalysisReply> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var reply = await _host.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        ObserveAssemblies(reply.AssemblyVersion);
        return reply;
    }

    /// <inheritdoc/>
    public async Task<long> WaitForAssembliesAsync(long version, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var changed = await _host.WaitForAssembliesAsync(version, cancellationToken).ConfigureAwait(false);
        ObserveAssemblies(changed);
        return AssemblyVersion;
    }

    /// <summary>
    /// The host process's id, so a test can end it mid-block.
    /// </summary>
    internal int ProcessId => _process.Id;

    /// <summary>
    /// Starts the host and waits for its hello.
    /// </summary>
    /// <param name="hostAssemblyPath">The host assembly, or null to locate it with <see cref="HostLocator"/>.</param>
    /// <param name="workingDirectory">The working directory for the host, or null for the current one.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running engine.</returns>
    /// <exception cref="HostProtocolException">The host could not be started or did not answer.</exception>
    public static Task<HostProcessEngine> StartAsync(string? hostAssemblyPath = null, string? workingDirectory = null,
        CancellationToken cancellationToken = default) => StartAsync(hostAssemblyPath, workingDirectory, null, cancellationToken);

    /// <summary>
    /// Starts an isolated host with environment overrides scoped to the child process.
    /// </summary>
    /// <param name="hostAssemblyPath">The host assembly location, or null for standard discovery.</param>
    /// <param name="workingDirectory">The initial host directory, or null for the caller's directory.</param>
    /// <param name="environment">Environment overrides applied only to the child host.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>The running engine.</returns>
    public static async Task<HostProcessEngine> StartAsync(string? hostAssemblyPath, string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken = default)
    {
        var lifetime = new HostProcessLifetime();
        try
        {
            return await StartCoreAsync(hostAssemblyPath, workingDirectory, environment, lifetime, true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await lifetime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Connects one host after its frontend-owned lifetime has acknowledged process ownership.
    /// </summary>
    /// <param name="hostAssemblyPath">The host assembly or null for discovery.</param>
    /// <param name="workingDirectory">The initial host directory.</param>
    /// <param name="environment">The child environment overrides.</param>
    /// <param name="lifetime">The reusable frontend lifetime.</param>
    /// <param name="ownsLifetime">Whether this engine disposes the lifetime.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>The connected engine.</returns>
    internal static async Task<HostProcessEngine> StartCoreAsync(string? hostAssemblyPath, string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment, HostProcessLifetime lifetime, bool ownsLifetime,
        CancellationToken cancellationToken)
    {
        var hostPath = hostAssemblyPath ?? HostLocator.FindHost();
        var listener = new LocalSocketListener();
        var startInfo = new ProcessStartInfo
        {
            FileName = HostLocator.FindDotnet(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add(hostPath);
        startInfo.ArgumentList.Add("--socket");
        startInfo.ArgumentList.Add(listener.SocketPath);
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        if (environment is not null)
        {
            foreach (var (name, value) in environment) startInfo.Environment[name] = value;
        }

        startInfo.Environment["ILREPL_HOST_HANDSHAKE"] = listener.Secret;
        using (var frontend = Process.GetCurrentProcess())
            startInfo.Environment["ILREPL_FRONTEND_OWNER"] = frontend.Id + ":" + OwnedProcessGroup.GetStartIdentity(frontend);
        OwnedHostProcess owned;
        try
        {
            owned = await lifetime.LaunchAsync(startInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
            or OperationCanceledException or ReplEngineException or RemoteInvocationException)
        {
            listener.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw new HostProtocolException($"could not start '{startInfo.FileName} {hostPath}': {ex.Message}", ex);
        }

        var process = owned.Process;
        var stderr = owned.Diagnostics;

        JsonRpc? rpc = null;
        Stream? connection = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var accepted = listener.AcceptAsync(process.Id, timeout.Token);
            var exited = OwnedProcessGroup.WaitForExitAsync(owned.Scope, timeout.Token);
            if (await Task.WhenAny(accepted, exited).ConfigureAwait(false) == exited)
            {
                await timeout.CancelAsync().ConfigureAwait(false);
                try { await accepted.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                throw new IOException("the host exited before connecting");
            }
            connection = await accepted.ConfigureAwait(false);
            rpc = new JsonRpc(RpcTransport.CreateHandler(connection, connection));
            var receiver = new HostEventReceiver();
            rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IReplClient>(), receiver, null);
            var host = rpc.Attach<IReplHost>();
            rpc.StartListening();
            var hello = await host.HelloAsync(timeout.Token).ConfigureAwait(false);
            var engine = new HostProcessEngine(process, rpc, host, stderr, hello, listener, connection,
                lifetime, owned.Scope, ownsLifetime);
            receiver.Client = engine;
            _ = engine.ObserveExitAsync();
            return engine;
        }
        catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException or OperationCanceledException or IOException)
        {
            string detail;
            lock (stderr)
            {
                detail = Tail(stderr.ToString());
            }

            rpc?.Dispose();
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            listener.Dispose();
            try
            {
                if (!process.HasExited)
                {
                    await lifetime.StopAsync(owned.Scope.Identity, CancellationToken.None).ConfigureAwait(false);
                }
                await OwnedProcessGroup.WaitForExitAsync(owned.Scope, CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw new HostProtocolException("the host did not answer: " + ex.Message + (detail.Length == 0 ? "" : "\n" + detail), ex);
        }
    }

    /// <inheritdoc/>
    public Task<HandleReply> HandleSourceAsync(string line, AnalysisLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        return CallAsync(token => _host.HandleSourceAsync(line, location, token), cancellationToken);
    }

    /// <inheritdoc />
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        return CallAsync(token => _host.HandleAsync(line, token), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<HandleReply> CompareAsync(string identity, CancellationToken cancellationToken) =>
        CallAsync(token => _host.CompareAsync(identity, token), cancellationToken, mutation: false);

    /// <inheritdoc/>
    public Task<HandleReply> InspectNativeAsync(string identity, CancellationToken cancellationToken) =>
        CallAsync(token => _host.InspectNativeAsync(identity, token), cancellationToken, mutation: false);

    /// <inheritdoc />
    public Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return CallAsync(token => _host.RollbackAsync(mark, token), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var reply = await _host.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            ObserveAssemblies(reply.AssemblyVersion);
            return reply;
        }
        catch (RemoteInvocationException exception)
        {
            throw new HostProtocolException("the host failed: " + exception.Message, exception);
        }
        catch (ConnectionLostException exception)
        {
            await ObserveConnectionLossAsync().ConfigureAwait(false);
            throw new HostProtocolException("the host exited" + ExitDetail(), exception);
        }
    }

    private async Task<HandleReply> CallAsync(Func<CancellationToken, Task<HandleReply>> call,
        CancellationToken cancellationToken, bool mutation = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var reply = mutation
                ? await InvokeMutationAsync(call, cancellationToken).ConfigureAwait(false)
                : await InvokeInspectionAsync(call, cancellationToken).ConfigureAwait(false);
            Status = reply.Status;
            return reply;
        }
        catch (RemoteInvocationException ex)
        {
            throw new HostProtocolException("the host failed: " + ex.Message, ex);
        }
        catch (ConnectionLostException ex)
        {
            await ObserveConnectionLossAsync().ConfigureAwait(false);
            throw new HostProtocolException("the host exited" + ExitDetail(), ex);
        }
    }

    private void ObserveAssemblies(long version)
    {
        var current = AssemblyVersion;
        while (version > current)
        {
            var previous = Interlocked.CompareExchange(ref _assemblyVersion, version, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    /// <summary>
    /// Closes the connection and waits briefly for the host to exit.
    /// </summary>
    /// <returns>A task that completes when the host is gone.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _expectedExit, 1);
        _rpc.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
        _listener.Dispose();
        try
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await OwnedProcessGroup.WaitForExitAsync(_scope, grace.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            await _lifetime.StopAsync(_scope.Identity, CancellationToken.None).ConfigureAwait(false);
            await OwnedProcessGroup.WaitForExitAsync(_scope, CancellationToken.None).ConfigureAwait(false);
            await _exit.Task.ConfigureAwait(false);
        }
        finally
        {
            _process.Dispose();
            if (_ownsLifetime) await _lifetime.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string ExitDetail()
    {
        string tail;
        lock (_stderr)
        {
            tail = Tail(_stderr.ToString());
        }

        var observed = _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result.ExitCode : null;
        var code = observed is { } value ? $" with code {value}" : " (exit status unavailable)";
        return code + (tail.Length == 0 ? "" : ":\n" + tail);
    }

    private static string Tail(string text) => text.Trim();
}
