using IlRepl.Protocol;

namespace IlRepl.Processes;

/// <summary>
/// Receives direct execution notifications without waiting for the outstanding submission reply.
/// </summary>
public sealed partial class HostProcessEngine : IReplClient, IHostedEngine, IProcessSupervision
{
    private ExecutionProgress _progress = new("", 0, ExecutionPhase.Cooperative, false);
    private int _expectedExit;

    /// <inheritdoc />
    public ExecutionProgress Progress => Volatile.Read(ref _progress);

    /// <inheritdoc />
    public event Action<ExecutionProgress>? ProgressChanged;

    /// <inheritdoc />
    public event Action<SessionReply>? CheckpointReceived;

    /// <inheritdoc />
    public event Action<ExecutionOutput>? OutputReceived;

    /// <inheritdoc />
    public event Action<HostExit>? Exited;

    /// <inheritdoc />
    public Task<bool> InterruptAsync(string identity, CancellationToken cancellationToken) =>
        _host.InterruptAsync(identity, cancellationToken);

    /// <inheritdoc />
    Task IReplClient.ExecutionChangedAsync(ExecutionProgress progress, CancellationToken cancellationToken)
    {
        var previous = Progress;
        while (progress.Sequence > previous.Sequence)
        {
            var observed = Interlocked.CompareExchange(ref _progress, progress, previous);
            if (ReferenceEquals(observed, previous))
            {
                ProgressChanged?.Invoke(progress);
                break;
            }
            previous = observed;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    Task IReplClient.CheckpointAsync(SessionReply checkpoint, CancellationToken cancellationToken)
    {
        if (AcceptCheckpoint(checkpoint) is { } accepted) CheckpointReceived?.Invoke(accepted);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    Task IReplClient.OutputAsync(ExecutionOutput output, CancellationToken cancellationToken)
    {
        OutputReceived?.Invoke(output);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task TerminateAsync(CancellationToken cancellationToken)
    {
        lock (_disposeLock)
            return _disposeTask is { } disposal ? disposal.WaitAsync(cancellationToken) : TerminateCoreAsync(cancellationToken);
    }

    private async Task TerminateCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _expectedExit, 1);
        try
        {
            await _lifetime.StopAsync(_scope.Identity, cancellationToken).ConfigureAwait(false);
            await OwnedProcessGroup.WaitForExitAsync(_scope, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Another shutdown path may already have observed and released this process.
        }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await OwnedProcessGroup.WaitForExitAsync(_scope, CancellationToken.None).ConfigureAwait(false);
            int? code;
            if (OperatingSystem.IsWindows()) code = _process.ExitCode;
            else code = await _lifetime.ExitCodeAsync(_scope.Identity).ConfigureAwait(false);
            string tail;
            lock (_stderr) tail = Tail(_stderr.ToString());
            var observed = new HostExit(_process.Id, code, tail, Volatile.Read(ref _expectedExit) != 0);
            Exited?.Invoke(observed);
            _exit.TrySetResult(observed);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // Disposal has already consumed the process lifetime.
        }
    }
    /// <inheritdoc />
    public ProcessSupervisionState Supervision => _lifetime.Supervision;

    /// <inheritdoc />
    public event Action<ProcessSupervisionState>? SupervisionChanged
    {
        add => _lifetime.SupervisionChanged += value;
        remove => _lifetime.SupervisionChanged -= value;
    }

    /// <inheritdoc />
    public Task RetrySupervisionAsync(CancellationToken cancellationToken) => _lifetime.RetrySupervisionAsync(cancellationToken);

    /// <inheritdoc />
    Task IReplClient.RegisterProcessAsync(OwnedProcessScope scope, CancellationToken cancellationToken) =>
        _lifetime.RegisterAsync(scope, _scope.Identity, cancellationToken);

    private async Task ObserveConnectionLossAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await _exit.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _lifetime.StopAsync(_scope.Identity, CancellationToken.None).ConfigureAwait(false);
            await _exit.Task.ConfigureAwait(false);
        }
    }
}
