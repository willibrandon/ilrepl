using System.Diagnostics;
using IlRepl.Protocol;

namespace IlRepl.Processes;

/// <summary>
/// Launches, adopts, and terminates scopes independently of the execution host's scheduler and locks.
/// </summary>
internal sealed class LifetimeSupervisorService(long epoch) : ISupervisorService, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, OwnedProcessScope> _scopes = [];
    private readonly Dictionary<string, Process> _children = [];
    private readonly HashSet<string> _acknowledged = [];
    private readonly CancellationTokenSource _lifetime = new();
    private long _revision;

    /// <summary>
    /// Preserves acknowledged groups when only the control connection failed and the frontend is still alive.
    /// </summary>
    internal bool PreserveOnDispose { get; set; }

    /// <inheritdoc />
    public async Task<string[]> AdoptAsync(SupervisorSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Validate(snapshot.Epoch);
            if (snapshot.Revision < _revision)
            {
                throw new InvalidOperationException("obsolete process ownership revision");
            }

            foreach (var identity in _scopes.Keys.Where(identity => !OwnedProcessGroup.IsCurrent(_scopes[identity])).ToArray())
            {
                _scopes.Remove(identity);
                _acknowledged.Remove(identity);
            }

            foreach (var scope in snapshot.Scopes)
            {
                if (!OwnedProcessGroup.IsCurrent(scope))
                {
                    continue;
                }

                _scopes[scope.Identity] = scope;
                _acknowledged.Add(scope.Identity);
            }

            _revision = snapshot.Revision;
            return [.. _scopes.Keys];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<OwnedProcessScope> LaunchAsync(SupervisorLaunch request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Validate(request.Epoch);
            var start = new ProcessStartInfo(request.Executable)
            {
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
            };
            foreach (var argument in request.Arguments)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment.Clear();
            foreach (var pair in request.Environment)
            {
                start.Environment[pair.Key] = pair.Value;
            }

            var process = Process.Start(start) ?? throw new IOException("the execution host did not start");
            process.StandardInput.Close();
            var scope = OwnedProcessGroup.Describe(process, request.Identity);
            _children.Add(scope.Identity, process);
            _scopes.Add(scope.Identity, scope);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                while (!OwnedProcessGroup.IsCurrent(scope))
                {
                    if (process.HasExited)
                    {
                        throw new IOException("the host exited before creating its process group");
                    }

                    await Task.Delay(10, timeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                throw;
            }

            return scope;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(string identity, long requestedEpoch, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Validate(requestedEpoch);
            await StopCoreAsync(identity).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int?> ExitCodeAsync(string identity, long requestedEpoch, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Validate(requestedEpoch);
            if (!_children.TryGetValue(identity, out var process))
            {
                return null;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var code = process.ExitCode;
            _children.Remove(identity);
            process.Dispose();
            return code;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task FlushDiagnosticsAsync(string marker, long requestedEpoch, CancellationToken cancellationToken)
    {
        Validate(requestedEpoch);
        cancellationToken.ThrowIfCancellationRequested();
        Console.Error.Write(marker);
        Console.Error.Flush();
        return Task.CompletedTask;
    }

    private void Validate(long requestedEpoch)
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        if (requestedEpoch != epoch)
        {
            throw new InvalidOperationException("obsolete lifetime supervisor generation");
        }
    }

    private async Task StopCoreAsync(string identity)
    {
        foreach (var scope in _scopes.Values.Where(scope => scope.Identity == identity || scope.ParentIdentity == identity).ToArray())
        {
            if (OwnedProcessGroup.IsCurrent(scope))
            {
                using var group = new OwnedProcessGroup();
                group.Adopt(scope.ProcessId);
                await group.StopAsync().ConfigureAwait(false);
            }

            if (_children.TryGetValue(scope.Identity, out var process))
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            _scopes.Remove(scope.Identity);
            _acknowledged.Remove(scope.Identity);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var identity in _scopes.Keys.Where(identity => !PreserveOnDispose || !_acknowledged.Contains(identity)).ToArray())
            {
                await StopCoreAsync(identity).ConfigureAwait(false);
            }

            foreach (var process in _children.Values)
            {
                process.Dispose();
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _lifetime.Dispose();
        }
    }
}
