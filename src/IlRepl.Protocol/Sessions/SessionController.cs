namespace IlRepl.Protocol;

/// <summary>
/// Coordinates document actions around a replaceable engine while keeping frontend identity stable.
/// </summary>
public sealed partial class SessionController : IReplEngine
{
    private IReplEngine _engine;
    private readonly Func<CancellationToken, Task<IReplEngine>> _start;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _replacement = new();
    private long _epoch;
    private bool _disposed;
    private readonly Lock _disposeLock = new();
    private Task? _disposeTask;
    private bool _checkpointBatch;

    /// <summary>
    /// Wraps the first engine and a factory that creates a fresh execution runtime.
    /// </summary>
    /// <param name="engine">The initial engine.</param>
    /// <param name="start">The replacement engine factory.</param>
    public SessionController(IReplEngine engine, Func<CancellationToken, Task<IReplEngine>> start)
    {
        _engine = engine;
        _start = start;
    }

    /// <summary>
    /// The current unsent editor snapshot contributed by the frontend.
    /// </summary>
    public SessionEditor Editor
    {
        get => _editor;
        set
        {
            _editor = value;
            EditorChanged?.Invoke(value);
        }
    }

    private SessionEditor _editor = new();

    /// <summary>
    /// Retains browser editor changes independently of source execution and worker responsiveness.
    /// </summary>
    public Action<SessionEditor>? EditorChanged { get; set; }

    /// <summary>
    /// Submitted lines still awaiting acceptance, retained as an unsent draft only in recovery checkpoints.
    /// </summary>
    public string[] PendingInput { get; set; } = [];

    /// <summary>
    /// Requests a path interactively, or remains null for noninteractive batch operation.
    /// </summary>
    public Func<bool, string, CancellationToken, Task<string?>>? RequestPathAsync { get; set; }

    /// <summary>
    /// Resolves unsaved replacement or file-associated quit, or remains null in batch mode.
    /// </summary>
    public Func<string?, CancellationToken, Task<SessionDecision>>? ConfirmUnsavedAsync { get; set; }

    /// <summary>
    /// Publishes a coherent source checkpoint and waits for the frontend to retain it before execution.
    /// </summary>
    public Func<SessionReply, CancellationToken, Task>? PublishCheckpointAsync { get; set; }

    /// <summary>
    /// Handles browser file interactions and worker replacement outside the worker's virtual filesystem.
    /// </summary>
    public Func<SessionRequest, CancellationToken, Task<SessionReply?>>? ExternalActionAsync { get; set; }

    /// <summary>
    /// The most recently captured workspace, used by frontend file controls and recovery.
    /// </summary>
    public SessionReply? Workspace { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> Catalog => _engine.Catalog;

    /// <inheritdoc />
    public CilVocabulary Vocabulary => _engine.Vocabulary;

    /// <inheritdoc />
    public SessionStatus Status => _engine.Status;

    /// <inheritdoc />
    public long AssemblyVersion => (Interlocked.Read(ref _epoch) << 32) | (_engine.AssemblyVersion & uint.MaxValue);

    /// <inheritdoc />
    public async Task<long> WaitForAssembliesAsync(long version, CancellationToken cancellationToken)
    {
        if (AssemblyVersion != version)
        {
            return AssemblyVersion;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _replacement.Token);
        try
        {
            await _engine.WaitForAssembliesAsync(version & uint.MaxValue, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A new runtime is itself an assembly catalog change.
        }

        return AssemblyVersion;
    }

    /// <inheritdoc />
    public async Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _replacement.Token);
        var reply = await _engine.CompleteAsync(request, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        return reply with { AssemblyVersion = AssemblyVersion };
    }

    /// <inheritdoc />
    public async Task<AnalysisReply> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _replacement.Token);
        var reply = await _engine.AnalyzeAsync(request, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        return reply with { AssemblyVersion = AssemblyVersion };
    }

    /// <inheritdoc />
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken) =>
        HandleCoreAsync(line, null, cancellationToken);

    /// <inheritdoc />
    public Task<HandleReply> HandleSourceAsync(string line, AnalysisLocation location, CancellationToken cancellationToken) =>
        HandleCoreAsync(line, location, cancellationToken);

    private async Task<HandleReply> HandleCoreAsync(string line, AnalysisLocation? location, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        HandleReply? reply = null;
        try
        {
            var boundary = RequiresCheckpoint(line);
            if (PublishCheckpointAsync is { } beforeExecution && (!_checkpointBatch || boundary))
            {
                var checkpoint = await CaptureAsync(cancellationToken).ConfigureAwait(false);
                var draft = checkpoint.Document.Editor with
                {
                    Lines = [line, .. PendingInput, .. checkpoint.Document.Editor.Lines], Caret = 0, Anchor = 0,
                };
                await beforeExecution(checkpoint with
                {
                    Document = checkpoint.Document with { Editor = draft },
                    PendingSubmission = Status.CellNumber, PendingSource = [line],
                }, cancellationToken)
                    .ConfigureAwait(false);
            }
            reply = location is null ? await _engine.HandleAsync(line, cancellationToken).ConfigureAwait(false)
                : await _engine.HandleSourceAsync(line, location, cancellationToken).ConfigureAwait(false);
            if (reply.SessionAction is { } action)
            {
                var result = await PerformAsync(new SessionRequest { Action = action, Editor = Editor }, cancellationToken)
                    .ConfigureAwait(false);
                Workspace = result;
                reply = result.Reply with { Lines = [.. reply.Lines, .. result.Reply.Lines], SessionAction = null };
            }
            else if (reply.Quit && !await CanLeaveAsync(quitting: true, force: false, cancellationToken).ConfigureAwait(false))
            {
                reply = reply with { Quit = false };
            }

            if (reply.SessionEditor is { } editor)
            {
                Editor = editor;
            }

            _checkpointBatch = PublishCheckpointAsync is not null && PendingInput.Length != 0 && reply.Succeeded
                && reply.SessionEditor is null && !reply.Quit;
            if (!_checkpointBatch || boundary) await CheckpointAsync(cancellationToken).ConfigureAwait(false);
            return reply;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or ReplEngineException)
        {
            _checkpointBatch = false;
            var failed = Failure(exception.Message);
            return failed with { Quit = _disposed, Lines = [.. reply?.Lines ?? [], .. failed.Lines],
                Diagnostics = reply?.Diagnostics ?? [] };
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return new HandleReply(false, true, reply?.Lines ?? [], Status);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SessionReply> SessionAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Editor = request.Editor;
            var result = await PerformAsync(request, cancellationToken).ConfigureAwait(false);
            Workspace = result;
            if (result.Reply.SessionEditor is { } editor)
            {
                Editor = editor;
            }

            await CheckpointAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SessionReply> PerformAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        if (ExternalActionAsync is { } external)
        {
            var current = await _engine.SessionAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.Capture }, Editor = request.Editor,
            }, cancellationToken).ConfigureAwait(false);
            if (await external(request with { Document = request.Document ?? current.Document }, cancellationToken)
                .ConfigureAwait(false) is { } handled)
            {
                if (request.Action.Operation == SessionOperation.Save && handled.Path is not null && handled.Reply.Succeeded)
                {
                    await _engine.SessionAsync(new SessionRequest
                    {
                        Action = new SessionAction { Operation = SessionOperation.AcknowledgeSave, Path = handled.Path },
                        Editor = request.Editor, Document = current.Document,
                    }, cancellationToken).ConfigureAwait(false);
                }

                return handled;
            }
        }

        var action = request.Action;
        if (action.Operation == SessionOperation.Quit)
        {
            var canLeave = await CanLeaveAsync(true, action.Force, cancellationToken).ConfigureAwait(false);
            var capture = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            return capture with { Reply = capture.Reply with { Quit = canLeave } };
        }

        if (action.Operation is SessionOperation.Open or SessionOperation.Save && action.Path is null)
        {
            var capture = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            var path = action.Operation == SessionOperation.Save ? capture.Path : null;
            if (path is null && RequestPathAsync is { } prompt)
            {
                path = await prompt(action.Operation == SessionOperation.Open, "session.ilrepl.json", cancellationToken)
                    .ConfigureAwait(false);
                if (path is null)
                {
                    return capture;
                }
            }

            action = action with { Path = path ?? throw new InvalidOperationException("a session path is required") };
            request = request with { Action = action };
        }

        if (action.Operation is SessionOperation.Open or SessionOperation.Hydrate or SessionOperation.Run or SessionOperation.Restore
            || (action.Operation == SessionOperation.Load && action.Reload))
        {
            if (action.Operation is SessionOperation.Open or SessionOperation.Hydrate
                && !await CanLeaveAsync(false, action.Force, cancellationToken).ConfigureAwait(false))
            {
                return await CaptureAsync(cancellationToken).ConfigureAwait(false);
            }

            var captured = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (action.Operation is SessionOperation.Run or SessionOperation.Load or SessionOperation.Restore)
            {
                request = request with { Document = request.Document ?? captured.Document,
                    AssociatedPath = captured.Path, Modified = captured.Dirty || action.Operation != SessionOperation.Restore };
                if (action.Operation == SessionOperation.Run)
                {
                    request = request with { Action = action with { Path = captured.Path } };
                }
            }

            var candidate = await _start(cancellationToken).ConfigureAwait(false);
            SessionReply result;
            try
            {
                // Presentation preferences belong to the frontend and are never imported from a document.
                if (!Status.Mark.EchoStack)
                {
                    await candidate.HandleAsync(".quiet on", cancellationToken).ConfigureAwait(false);
                }

                if (Status.Mark.ShowTiming)
                {
                    await candidate.HandleAsync(".time on", cancellationToken).ConfigureAwait(false);
                }

                if (PublishCheckpointAsync is { } publish)
                {
                    await publish(action.Operation == SessionOperation.Run
                        ? captured with { PendingSubmission = action.Numbers.FirstOrDefault(
                            captured.Document.Cells.FirstOrDefault(cell => cell.Kind == "cell")?.Number ?? Status.CellNumber),
                            PendingSource = captured.Document.Editor.Lines }
                        : captured, cancellationToken).ConfigureAwait(false);
                }

                if (action.Operation == SessionOperation.Run)
                {
                    (candidate, result) = await RunCandidateAsync(candidate, request, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await candidate.SessionAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var previous = _engine;
            _engine = candidate;
            Interlocked.Increment(ref _epoch);
            var previousCancellation = _replacement;
            _replacement = new CancellationTokenSource();
            await previousCancellation.CancelAsync().ConfigureAwait(false);
            previousCancellation.Dispose();
            await previous.DisposeAsync().ConfigureAwait(false);
            return result;
        }

        return await _engine.SessionAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CanLeaveAsync(bool quitting, bool force, CancellationToken cancellationToken)
    {
        if (force)
        {
            return true;
        }

        var current = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Dirty || (quitting && (current.Path is null || ConfirmUnsavedAsync is null)))
        {
            return true;
        }

        if (ConfirmUnsavedAsync is not { } confirm)
        {
            throw new InvalidOperationException("the session has unsaved source; save it or use .session open <path> --force");
        }

        var choice = await confirm(current.Path, cancellationToken).ConfigureAwait(false);
        if (choice == SessionDecision.Save)
        {
            var saved = await PerformAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.Save },
                Editor = Editor,
            }, cancellationToken).ConfigureAwait(false);
            return !saved.Dirty;
        }

        return choice == SessionDecision.Discard;
    }

    private async Task<SessionReply> CaptureAsync(CancellationToken cancellationToken)
    {
        Workspace = await _engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture },
            Editor = Editor,
        }, cancellationToken).ConfigureAwait(false);
        return Workspace;
    }

    private async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        if (PublishCheckpointAsync is { } publish)
        {
            var captured = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (PendingInput.Length != 0)
            {
                var editor = captured.Document.Editor with { Lines = [.. PendingInput, .. captured.Document.Editor.Lines] };
                captured = captured with { Document = captured.Document with { Editor = editor } };
            }

            await publish(captured, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool RequiresCheckpoint(string line)
    {
        var comment = Status.Mark.InBlockComment;
        var kind = CilLexer.Classify(line, ref comment, out var text);
        // Ordinary instructions cannot execute until a run boundary. A batch's first checkpoint
        // already retains every unsent line, so intermediate source can stay in the host until then.
        return kind == SourceLineKind.Blank || (kind == SourceLineKind.Text
            && (text.StartsWith('.') || text.StartsWith('}') || text == "ret" || text.StartsWith("ret ", StringComparison.Ordinal)));
    }

    /// <inheritdoc />
    public async Task<HandleReply> CompareAsync(string identity, CancellationToken cancellationToken)
    {
        await CheckpointAsync(cancellationToken).ConfigureAwait(false);
        return await _engine.CompareAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HandleReply> InspectNativeAsync(string identity, CancellationToken cancellationToken)
    {
        await CheckpointAsync(cancellationToken).ConfigureAwait(false);
        return await _engine.InspectNativeAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reply = await _engine.RollbackAsync(mark, cancellationToken).ConfigureAwait(false);
            await CheckpointAsync(cancellationToken).ConfigureAwait(false);
            return reply;
        }
        finally
        {
            _gate.Release();
        }
    }

    private HandleReply Failure(string message) => new(false, false,
        [TranscriptLine.Of(LineKind.Error, "  error: " + message, SpanStyle.Error)], Status);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        CancelExecution();
        await _replacement.CancelAsync().ConfigureAwait(false);
        await _engine.DisposeAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        _replacement.Dispose();
        _gate.Dispose();
        _lifetime.Dispose();
    }
}
