using System.Collections.Concurrent;
using System.Diagnostics;
using IlRepl.Protocol;
using StreamJsonRpc;

namespace IlRepl.Processes;

/// <summary>
/// Owns one frontend's hosts and descendant scopes across runtime replacement and supervisor adoption.
/// </summary>
public sealed class HostProcessLifetime : IProcessSupervision, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, OwnedProcessScope> _scopes = [];
    private readonly Dictionary<string, OwnedProcessGroup> _jobs = [];
    private readonly List<SupervisorConnection> _connections = [];
    private readonly ConcurrentDictionary<string, SupervisorConnection> _diagnosticOwners = new();
    private readonly CancellationTokenSource _lifetime = new();
    private SupervisorConnection? _current;
    private ProcessSupervisionState _state = new(0, false, false, null);
    private long _epoch;
    private long _revision;
    private bool _disposed;
    private bool _stopping;
    private readonly object _stopLock = new();
    private Task? _stopTask;
    private readonly string? _supervisorAssemblyPath;

    /// <summary>
    /// Creates independent frontend ownership using the currently executing frontend package.
    /// </summary>
    public HostProcessLifetime()
    {
    }

    /// <summary>
    /// Uses an isolated copy of the real frontend for process-start and package-loss regression coverage.
    /// </summary>
    /// <param name="supervisorAssemblyPath">The actual frontend assembly to launch in private mode.</param>
    internal HostProcessLifetime(string supervisorAssemblyPath) => _supervisorAssemblyPath = supervisorAssemblyPath;

    /// <inheritdoc />
    public ProcessSupervisionState Supervision => Volatile.Read(ref _state);

    /// <inheritdoc />
    public event Action<ProcessSupervisionState>? SupervisionChanged;

    /// <summary>
    /// Starts a host while reusing this frontend's lifetime supervisor or Windows ownership scope.
    /// </summary>
    /// <param name="hostAssemblyPath">The host assembly, or null for discovery.</param>
    /// <param name="workingDirectory">The host's initial directory.</param>
    /// <param name="environment">Environment overrides applied only to this host.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>The connected engine, whose disposal leaves this lifetime reusable.</returns>
    public Task<HostProcessEngine> StartAsync(string? hostAssemblyPath = null, string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default) =>
        HostProcessEngine.StartCoreAsync(hostAssemblyPath, workingDirectory, environment, this, false, cancellationToken);

    /// <summary>
    /// The active supervisor process identifier, for real process-lifetime regression tests.
    /// </summary>
    internal int? SupervisorProcessId => _current?.Process.Id;

    /// <summary>
    /// Launches a root and acknowledges its ownership before the host's execution handshake can complete.
    /// </summary>
    /// <param name="start">The exact launch configuration.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>The owned host and direct diagnostic sink.</returns>
    internal async Task<OwnedHostProcess> LaunchAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || _stopping, this);
            var identity = Guid.NewGuid().ToString("N");
            if (OperatingSystem.IsWindows())
            {
                var process = Process.Start(start) ?? throw new IOException("the execution host did not start");
                process.StandardInput.Close();
                var group = new OwnedProcessGroup();
                group.Attach(process);
                _jobs.Add(identity, group);
                var scope = OwnedProcessGroup.Describe(process, identity);
                _scopes.Add(identity, scope);
                var diagnostics = new DiagnosticTail();
                _ = DiagnosticTail.DrainAsync(process.StandardError, () => diagnostics);
                _ = DiagnosticTail.DrainAsync(process.StandardOutput, () => diagnostics);
                return new OwnedHostProcess(process, scope, diagnostics);
            }
            await EnsureSupervisorCoreAsync(cancellationToken).ConfigureAwait(false);
            var current = _current!;
            var buffer = current.BeginDiagnostics();
            var launched = await current.Service.LaunchAsync(new SupervisorLaunch(current.Epoch, identity,
                start.FileName, [.. start.ArgumentList], start.WorkingDirectory,
                new Dictionary<string, string?>(start.Environment)), cancellationToken).ConfigureAwait(false);
            _scopes.Add(identity, launched);
            _diagnosticOwners[identity] = current;
            _revision++;
            await AdoptCoreAsync(current, cancellationToken).ConfigureAwait(false);
            return new OwnedHostProcess(Process.GetProcessById(launched.ProcessId), launched, buffer);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Registers a prepared worker before its parent releases user execution.
    /// </summary>
    /// <param name="scope">The stable child group identity.</param>
    /// <param name="parent">The runtime identity assigned by the frontend.</param>
    /// <param name="cancellationToken">Cancels acknowledgement.</param>
    /// <returns>Completion after ownership is acknowledged.</returns>
    internal async Task RegisterAsync(OwnedProcessScope scope, string parent, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || _stopping, this);
            _scopes[scope.Identity] = scope with { ParentIdentity = parent };
            _revision++;
            if (!OperatingSystem.IsWindows())
            {
                await EnsureSupervisorCoreAsync(cancellationToken).ConfigureAwait(false);
                await AdoptCoreAsync(_current!, cancellationToken).ConfigureAwait(false);
                if (!_scopes.ContainsKey(scope.Identity)) throw new IOException("the worker exited before ownership acknowledgement");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Waits for adoption before dispatching new user execution.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Completion when execution may be dispatched.</returns>
    internal async Task WaitForDispatchAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || _stopping, this);
            if (!OperatingSystem.IsWindows()) await EnsureSupervisorCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task RetrySupervisionAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_current is not null && !_current.Process.HasExited && !_current.Rpc.Completion.IsCompleted
                && !Supervision.Degraded) return;
            await RestoreCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureSupervisorCoreAsync(CancellationToken cancellationToken)
    {
        if (_current is not null && !_current.Process.HasExited && !_current.Rpc.Completion.IsCompleted) return;
        if (Supervision.Degraded) throw new ReplEngineException(Supervision.Detail ?? "process supervision is unavailable");
        await RestoreCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        SetState(new ProcessSupervisionState(++_epoch, true, false, "Restoring process supervision."));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var connection = await SupervisorConnection.StartAsync(_epoch, _supervisorAssemblyPath, timeout.Token).ConfigureAwait(false);
            _connections.Add(connection);
            await AdoptCoreAsync(connection, timeout.Token).ConfigureAwait(false);
            _current = connection;
            SetState(new ProcessSupervisionState(_epoch, false, false, null));
            _ = ObserveSupervisorAsync(connection);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or RemoteInvocationException)
        {
            _current = null;
            SetState(new ProcessSupervisionState(_epoch, false, true,
                "Process supervision could not be restored. Retry supervision to run more code; the current runtime is preserved."));
            throw new ReplEngineException(Supervision.Detail!, exception);
        }
    }

    private async Task AdoptCoreAsync(SupervisorConnection connection, CancellationToken cancellationToken)
    {
        var retained = await connection.Service.AdoptAsync(new SupervisorSnapshot(connection.Epoch, ++_revision,
            [.. _scopes.Values]), cancellationToken).ConfigureAwait(false);
        var acknowledged = retained.ToHashSet(StringComparer.Ordinal);
        foreach (var identity in _scopes.Keys.Where(identity => !acknowledged.Contains(identity)).ToArray())
            _scopes.Remove(identity);
    }

    private async Task ObserveSupervisorAsync(SupervisorConnection connection)
    {
        try
        {
            await Task.WhenAny(connection.Process.WaitForExitAsync(_lifetime.Token), connection.Rpc.Completion).ConfigureAwait(false);
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (_disposed || !ReferenceEquals(_current, connection)) return;
                _current = null;
                await RestoreCoreAsync(_lifetime.Token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ReplEngineException or ObjectDisposedException) { }
    }

    /// <summary>
    /// Stops all registered groups belonging to one runtime, including orphaned worker descendants.
    /// </summary>
    /// <param name="identity">The runtime scope identity.</param>
    /// <param name="cancellationToken">Cancels the wait for the ownership gate.</param>
    /// <returns>Completion after every owned group has stopped.</returns>
    internal async Task StopAsync(string identity, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_jobs.Remove(identity, out var job))
            {
                using (job) await job.StopAsync().ConfigureAwait(false);
            }
            else if (!OperatingSystem.IsWindows())
            {
                foreach (var scope in _scopes.Values.Where(scope => scope.Identity == identity || scope.ParentIdentity == identity))
                {
                    if (!OwnedProcessGroup.IsCurrent(scope)) continue;
                    using var group = new OwnedProcessGroup();
                    group.Adopt(scope.ProcessId);
                    await group.StopAsync().ConfigureAwait(false);
                }
            }
            if (_current is { } current && !current.Rpc.Completion.IsCompleted)
            {
                try { await current.Service.StopAsync(identity, current.Epoch, cancellationToken).ConfigureAwait(false); }
                catch (ConnectionLostException) { }
            }
            foreach (var scope in _scopes.Values.Where(scope => scope.Identity == identity || scope.ParentIdentity == identity).ToArray())
                _scopes.Remove(scope.Identity);
            _revision++;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Queries the process parent for an exit status without inventing one after adoption.
    /// </summary>
    /// <param name="identity">The host scope identity.</param>
    /// <returns>The available native exit status.</returns>
    internal async Task<int?> ExitCodeAsync(string identity)
    {
        if (!_diagnosticOwners.TryRemove(identity, out var original)) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var code = original.Process.HasExited ? null
                : await original.Service.ExitCodeAsync(identity, original.Epoch, timeout.Token).ConfigureAwait(false);
            await original.DrainDiagnosticsAsync(timeout.Token).ConfigureAwait(false);
            return code;
        }
        catch (Exception exception) when (exception is ConnectionLostException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    private void SetState(ProcessSupervisionState state)
    {
        Volatile.Write(ref _state, state);
        SupervisionChanged?.Invoke(state);
    }

    /// <summary>
    /// Terminates every owned runtime and worker for batch interruption without entering an engine execution gate.
    /// </summary>
    /// <param name="cancellationToken">Bounds observation while cleanup continues to completion.</param>
    /// <returns>Completion after all acknowledged process groups have stopped.</returns>
    public Task TerminateAsync(CancellationToken cancellationToken)
    {
        Task stopped;
        lock (_stopLock) stopped = _stopTask ??= StopAllAsync();
        return stopped.WaitAsync(cancellationToken);
    }

    private async Task StopAllAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        OwnedProcessScope[] roots;
        try
        {
            _stopping = true;
            roots = [.. _scopes.Values.Where(scope => scope.ParentIdentity is null)];
        }
        finally { _gate.Release(); }
        foreach (var scope in roots) await StopAsync(scope.Identity, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        SupervisorConnection[] connections;
        try
        {
            if (_disposed) return;
            _disposed = true;
            connections = [.. _connections];
        }
        finally { _gate.Release(); }
        foreach (var connection in connections) await connection.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
