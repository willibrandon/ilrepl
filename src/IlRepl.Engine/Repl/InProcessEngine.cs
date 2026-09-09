using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// An engine that runs <see cref="ReplCore"/> in the current process. The browser build uses it;
/// the Native AOT tool talks to the same core through the host process instead.
/// </summary>
public sealed class InProcessEngine : IReplEngine
{
    private readonly ReplCore _core;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenSource _warmupCancellation = new();
    private readonly Task _warmup;
    private bool _disposed;

    /// <summary>
    /// Initializes an engine over a new session.
    /// </summary>
    public InProcessEngine() : this(new ReplCore())
    {
    }

    /// <summary>
    /// Initializes an engine over the given core.
    /// </summary>
    /// <param name="core">The REPL core.</param>
    public InProcessEngine(ReplCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
        Status = core.Status;
        _core.Session.CompletionChanged += CancelWarmup;
        _warmup = WarmAsync();
    }

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> Catalog => Completer.Catalog;

    /// <inheritdoc />
    public CilVocabulary Vocabulary => CilVocabularyBuilder.Vocabulary;

    /// <inheritdoc />
    public SessionStatus Status { get; private set; }

    /// <inheritdoc />
    public async Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _warmupCancellation.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return Reply(_core.Handle(line));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return Reply(_core.Rollback(mark));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _warmup.WaitAsync(cancellation.Token).ConfigureAwait(false);
        await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            return await _core.CompleteAsync(request, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Captures the initial catalog and status under the same gate as input and completion.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the engine.</param>
    /// <returns>The coherent initial snapshot.</returns>
    public async Task<HostHello> HelloAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new HostHello(Catalog, Vocabulary, _core.Status);
        }
        finally
        {
            _gate.Release();
        }
    }

    private HandleReply Reply(HandleResult result)
    {
        var lines = _core.Transcript.Lines.ToArray();
        _core.Transcript.Clear();
        Status = _core.Status;
        return new HandleReply(result.Succeeded, result.QuitRequested, lines, Status);
    }

    private async Task WarmAsync()
    {
        await Task.Yield();
        try
        {
            BindingSnapshot snapshot;
            await _gate.WaitAsync(_warmupCancellation.Token).ConfigureAwait(false);
            try
            {
                _warmupCancellation.Token.ThrowIfCancellationRequested();
                snapshot = BindingSnapshot.Capture(_core.Session);
            }
            finally
            {
                _gate.Release();
            }

            await CompletionWarmup.RunAsync(snapshot, _warmupCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_warmupCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            // Optional eager work does not turn an infrastructure failure into a cached empty query.
            // The foreground request still performs its normal guarded capture and reports any failure.
        }
    }

    private void CancelWarmup() => _warmupCancellation.Cancel();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _core.Session.CompletionChanged -= CancelWarmup;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _warmupCancellation.CancelAsync().ConfigureAwait(false);
        await _warmup.ConfigureAwait(false);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _core.Dispose();
        }
        finally
        {
            _gate.Release();
        }

        _shutdown.Dispose();
        _warmupCancellation.Dispose();
    }
}
