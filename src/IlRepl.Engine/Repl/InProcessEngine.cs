using IlRepl.Engine;
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
    private readonly Lock _analysisLock = new();
    private readonly HashSet<Task> _analyses = [];
    private AnalyzedDocument? _analysisCache;

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

    /// <inheritdoc/>
    public long AssemblyVersion => ProcessAssemblies.Version;

    /// <inheritdoc/>
    public Task<AnalysisReply> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Line);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Line, request.Lines.Count);
        request = request with { Lines = [.. request.Lines] };
        Task<AnalysisReply> analysis;
        lock (_analysisLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            analysis = AnalyzeCoreAsync(request, cancellationToken);
            _analyses.Add(analysis);
        }

        _ = analysis.ContinueWith(RemoveAnalysis, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return analysis;
    }

    private async Task<AnalysisReply> AnalyzeCoreAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await Task.Yield();
        await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        EditingSeed seed;
        long version;
        try
        {
            version = AssemblyVersion;
            lock (_analysisLock)
            {
                if (_analysisCache is { } cached && cached.Reply.Revision == _core.Status.Revision
                    && cached.Reply.AssemblyVersion == version && cached.Lines.SequenceEqual(request.Lines))
                {
                    return cached.At(request);
                }
            }

            seed = _core.Session.CaptureEditingSeed();
        }
        finally
        {
            _gate.Release();
        }

        using var editing = new EditingSession(seed);
        var reply = await editing.AnalyzeAsync(request, cancellation.Token).ConfigureAwait(false);
        reply = reply with { AssemblyVersion = version };
        lock (_analysisLock)
        {
            _analysisCache = !_disposed && editing.AnalyzedDocument is { } document ? document with { Reply = reply } : null;
        }

        return reply;
    }

    private void RemoveAnalysis(Task analysis)
    {
        lock (_analysisLock)
        {
            _analyses.Remove(analysis);
        }
    }

    /// <inheritdoc/>
    public async Task<long> WaitForAssembliesAsync(long version, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        return await ProcessAssemblies.WaitForChangeAsync(version, cancellation.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken) =>
        HandleLineAsync(line, null, cancellationToken);

    /// <inheritdoc/>
    public Task<HandleReply> HandleSourceAsync(string line, AnalysisLocation location, CancellationToken cancellationToken) =>
        HandleLineAsync(line, location, cancellationToken);

    private async Task<HandleReply> HandleLineAsync(string line, AnalysisLocation? location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        await _warmupCancellation.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Reply(_core.Handle(line, location));
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            var version = AssemblyVersion;
            var reply = await _core.CompleteAsync(request, cancellation.Token).ConfigureAwait(false);
            return reply with { AssemblyVersion = version };
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
            return new HostHello(Catalog, Vocabulary, _core.Status, AssemblyVersion);
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
        return new HandleReply(result.Succeeded, result.QuitRequested, lines, Status) { Diagnostics = result.Diagnostics };
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
        Task[] analyses;
        lock (_analysisLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _analysisCache = null;
            analyses = [.. _analyses];
        }
        _core.Session.CompletionChanged -= CancelWarmup;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(analyses).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
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
