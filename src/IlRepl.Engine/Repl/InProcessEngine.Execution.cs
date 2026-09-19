using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Owns synchronous execution, cooperative interruption, and immutable committed editing snapshots.
/// </summary>
public sealed partial class InProcessEngine
{
    private readonly Lock _operationLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly AsyncLocal<CancellationTokenSource?> _ambientOperation = new();
    private CancellationTokenSource? _operationCancellation;
    private ExecutionProgress _progress = new("", 0, ExecutionPhase.Cooperative, false);
    private long _progressSequence;

    /// <summary>
    /// The latest operation phase, available without waiting for user execution.
    /// </summary>
    public ExecutionProgress Progress
    {
        get
        {
            lock (_operationLock)
            {
                return _progress;
            }
        }
    }

    /// <summary>
    /// Publishes operation transitions independently of the serialized execution queue.
    /// </summary>
    public event Action<ExecutionProgress>? ProgressChanged;

    /// <summary>
    /// Acknowledges operation transitions through an optional asynchronous host transport before execution continues.
    /// </summary>
    public Func<ExecutionProgress, Task>? ProgressPublisher { get; set; }

    /// <summary>
    /// Requests cooperative cancellation only when the named operation is still running.
    /// </summary>
    public async Task<bool> InterruptAsync(string identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task cancellation;
        ExecutionProgress progress;
        lock (_operationLock)
        {
            if (_progress is not { IsRunning: true } active || active.Identity != identity || _operationCancellation is null)
            {
                return false;
            }

            _progress = progress = active with { Sequence = ++_progressSequence, CancellationRequested = true };
            cancellation = _operationCancellation.CancelAsync();
        }

        await PublishProgressAsync(progress).ConfigureAwait(false);
        await cancellation.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Runs cooperative host tooling under observable cancellation while allowing nested engine calls to share its operation.
    /// </summary>
    /// <param name="name">The operation name shown while cancellation settles.</param>
    /// <param name="action">The engine-owned asynchronous work.</param>
    /// <param name="cancellationToken">Cancels this operation and its nested engine work.</param>
    /// <returns>The actual terminal result of the operation.</returns>
    public async Task<T> RunOperationAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (_ambientOperation.Value is { } parent)
        {
            using var nested = CancellationTokenSource.CreateLinkedTokenSource(parent.Token, cancellationToken);
            return await action(nested.Token).ConfigureAwait(false);
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _operationGate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            ExecutionProgress started;
            lock (_operationLock)
            {
                _operationCancellation = operation;
                _progress = started = new ExecutionProgress(Guid.NewGuid().ToString("N"), ++_progressSequence,
                    ExecutionPhase.Cooperative, true, Name: name);
            }

            _ambientOperation.Value = operation;
            try
            {
                await PublishProgressAsync(started).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                return await action(operation.Token).ConfigureAwait(false);
            }
            finally
            {
                _ambientOperation.Value = null;
                ExecutionProgress finished;
                lock (_operationLock)
                {
                    _operationCancellation = null;
                    _progress = finished = _progress with
                    {
                        Sequence = ++_progressSequence, Phase = ExecutionPhase.Cleanup, IsRunning = false,
                    };
                }

                await PublishProgressAsync(finished).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private Task<T> ExecuteOperationAsync<T>(
        string name,
        Func<CancellationToken, T> action,
        CancellationToken cancellationToken) => RunOperationAsync(name,
            token => ExecuteAsync(() => action(token), token), cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            T Execute()
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return action();
                }
                finally
                {
                    PublishEditingSeed();
                }
            }

            return _execution is null ? Execute()
                : await _execution.RunAsync(Execute, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReportPhase(ExecutionPhase phase)
    {
        ExecutionProgress progress;
        lock (_operationLock)
        {
            if (_progress is not { IsRunning: true } active)
            {
                return;
            }

            _progress = progress = active with { Sequence = ++_progressSequence, Phase = phase };
        }

        PublishProgressAsync(progress).GetAwaiter().GetResult();
    }

    private Task PublishProgressAsync(ExecutionProgress progress)
    {
        ProgressChanged?.Invoke(progress);
        return ProgressPublisher?.Invoke(progress) ?? Task.CompletedTask;
    }

    private void PublishEditingSeed()
    {
        var status = _core.Status;
        if (_publishedSeed.Revision == status.Revision && _publishedSeed.AssemblyVersion == AssemblyVersion)
        {
            Status = status;
            return;
        }

        var version = AssemblyVersion;
        var captured = _core.CaptureEditingSeed() with { AssemblyVersion = version };
        lock (_snapshotLock)
        {
            var previous = _publishedSeed;
            _publishedSeed = captured;
            Status = status;
            previous.Dispose();
        }
    }

    private EditingSeed CapturePublishedSeed()
    {
        // Only tracked editing tasks capture here; disposal retains the published seed until those tasks have settled.
        lock (_snapshotLock)
        {
            // Keep source frozen while user code runs; only newly searchable metadata can join its independently owned catalog.
            var version = AssemblyVersion;
            if (_publishedSeed.AssemblyVersion != version)
            {
                var previous = _publishedSeed;
                _publishedSeed = previous with
                {
                    Snapshot = previous.Snapshot.WithProcessAssemblies(ProcessAssemblies.Current), AssemblyVersion = version,
                };
                previous.Dispose();
            }

            return _publishedSeed.Lease();
        }
    }
}
