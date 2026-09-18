using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Runs the shared REPL core inside the current process with serialized submission and workspace operations.
/// </summary>
/// <remarks>
/// An engine that runs <see cref="ReplCore"/> in the current process. The browser build uses it;
/// the Native AOT tool talks to the same core through the host process instead.
/// </remarks>
public sealed partial class InProcessEngine : IReplEngine, IInterruptibleEngine
{
    private readonly ReplCore _core;
    private readonly ExecutionThread? _execution;
    private readonly OperandCompleter _completion;
    private readonly Lock _snapshotLock = new();
    private EditingSeed _publishedSeed;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenSource _warmupCancellation = new();
    private readonly Task _warmup;
    private bool _disposed;
    private readonly Lock _analysisLock = new();
    private readonly HashSet<Task> _analyses = [];
    private AnalyzedDocument? _analysisCache;
    private readonly Func<ComparisonPackage, CancellationToken, Task<ComparisonReply>>? _comparisonRunner;
    private (ComparisonTicket Ticket, ComparisonPackage Package, MethodEdit Edit)? _preparedComparison;
    private readonly Func<NativePackage, CancellationToken, Task<NativeReply>>? _nativeRunner;
    private (NativeTicket Ticket, NativePackage Package, long Revision)? _preparedNative;

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
    public InProcessEngine(ReplCore core) : this(core, null)
    {
    }

    /// <summary>
    /// Initializes an engine with the host or browser coordinator that provides fresh comparison runtimes.
    /// </summary>
    /// <param name="core">The live REPL core.</param>
    /// <param name="comparisonRunner">The isolated execution coordinator, or null when comparisons are unavailable.</param>
    /// <param name="nativeRunner">The isolated native compilation coordinator.</param>
    public InProcessEngine(ReplCore core, Func<ComparisonPackage, CancellationToken, Task<ComparisonReply>>? comparisonRunner,
        Func<NativePackage, CancellationToken, Task<NativeReply>>? nativeRunner = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
        _execution = OperatingSystem.IsBrowser() ? null : new ExecutionThread();
        var version = AssemblyVersion;
        _publishedSeed = core.CaptureEditingSeed() with { AssemblyVersion = version };
        _completion = new OperandCompleter(CapturePublishedSeed);
        _core.PhaseChanged = ReportPhase;
        _comparisonRunner = comparisonRunner;
        _nativeRunner = nativeRunner;
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
        cancellation.Token.ThrowIfCancellationRequested();
        using var seed = CapturePublishedSeed();
        var version = seed.AssemblyVersion;
        lock (_analysisLock)
        {
            if (_analysisCache is { } cached && cached.Reply.Revision == seed.Revision
                && cached.Reply.AssemblyVersion == version && cached.Lines.SequenceEqual(request.Lines))
            {
                return cached.At(request);
            }
        }

        using var editing = new EditingSession(seed.Lease(), cancellation.Token);
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
        var reply = await ExecuteOperationAsync("submission", operation =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = _core.HandleCancellable(line, location, operation);
            var reply = Reply(result);
            if (result.ComparisonPackage is { } package)
            {
                var ticket = new ComparisonTicket(Guid.NewGuid().ToString("N"), package.Name, package.StartingState);
                _preparedComparison = (ticket, package, _core.Session.Edits.Single(edit => edit.Name == package.Name));
                reply = reply with { PendingComparison = ticket };
            }

            if (result.NativePackage is { } native)
            {
                var ticket = new NativeTicket { Identity = Guid.NewGuid().ToString("N"), Name = native.Left.Name };
                _preparedNative = (ticket, native, _core.Status.Revision);
                reply = reply with { PendingNative = ticket };
            }
            return reply;
        }, cancellationToken).ConfigureAwait(false);

        if (reply.SessionAction is { Operation: SessionOperation.Load, Reload: false } action && SessionTooling is { } tooling)
        {
            try
            {
                var loaded = await RunOperationAsync("load", async token =>
                {
                    var workspace = await tooling(new SessionRequest { Action = action }, token).ConfigureAwait(false);
                    WorkspaceCheckpoint?.Invoke(workspace);
                    return workspace;
                }, cancellationToken).ConfigureAwait(false);
                return loaded.Reply with { Lines = [.. reply.Lines, .. loaded.Reply.Lines] };
            }
            catch (Exception exception) when (exception is ReplException or IOException or InvalidDataException or ArgumentException)
            {
                return new HandleReply(false, false,
                    [.. reply.Lines, TranscriptLine.Of(LineKind.Error, "  " + exception.Message, SpanStyle.Error)], Status);
            }
        }

        return reply;
    }

    /// <inheritdoc />
    public async Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await ExecuteAsync(() => Reply(_core.Rollback(mark)), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        Task<CompletionReply> completion;
        lock (_analysisLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            completion = CompleteCoreAsync(request, cancellationToken);
            _analyses.Add(completion);
        }

        _ = completion.ContinueWith(RemoveAnalysis, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion;
    }

    private async Task<CompletionReply> CompleteCoreAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await Task.Yield();
        cancellation.Token.ThrowIfCancellationRequested();
        return await _completion.CompleteAsync(request, cancellation.Token).ConfigureAwait(false);
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
        return new HandleReply(result.Succeeded, result.QuitRequested, lines, Status)
        {
            Diagnostics = result.Diagnostics,
            EditDocument = result.EditDocument,
            Diff = result.Diff,
            SessionAction = result.SessionAction,
            AssemblyExport = result.AssemblyExport,
        };
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
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            // The request already observes its failure; shutdown must still release snapshots and the execution thread.
        }
        await _warmupCancellation.CancelAsync().ConfigureAwait(false);
        await _warmup.ConfigureAwait(false);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_execution is null)
            {
                _core.Dispose();
            }
            else
            {
                await _execution.RunAsync(() => { _core.Dispose(); return true; }, CancellationToken.None).ConfigureAwait(false);
            }
            _completion.Dispose();
            lock (_snapshotLock)
            {
                _publishedSeed.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (_execution is not null)
        {
            await _execution.DisposeAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
        _warmupCancellation.Dispose();
    }
}
