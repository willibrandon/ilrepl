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
    private int _historyLineLimit;

    /// <summary>
    /// Wraps the first engine and a factory that creates a fresh execution runtime.
    /// </summary>
    /// <param name="engine">The initial engine.</param>
    /// <param name="start">The replacement engine factory.</param>
    /// <param name="historyLineLimit">The historical presentation row limit, or zero for unlimited output.</param>
    public SessionController(IReplEngine engine, Func<CancellationToken, Task<IReplEngine>> start, int historyLineLimit = 0)
    {
        HistoryLineLimit = historyLineLimit;
        _engine = engine;
        _start = start;
        ObserveEngine(engine);
    }

    /// <summary>
    /// Limits restored presentation history without limiting the retained source journal; zero keeps every row.
    /// </summary>
    public int HistoryLineLimit
    {
        get => Volatile.Read(ref _historyLineLimit);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Volatile.Write(ref _historyLineLimit, value);
        }
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
    /// Retains editor changes independently of source execution and runtime responsiveness.
    /// </summary>
    public Action<SessionEditor>? EditorChanged { get; set; }

    /// <summary>
    /// Submitted lines still awaiting acceptance, retained as an unsent draft only in recovery checkpoints.
    /// </summary>
    public string[] PendingInput { get; set; } = [];

    /// <summary>
    /// Additional submitted buffers awaiting the current submission, retained in typing order during runtime recovery.
    /// </summary>
    public string[] QueuedInput { get; set; } = [];

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

        var (engine, epoch, cancellation) = CaptureRuntime(cancellationToken);
        using var linked = cancellation;
        if ((version >> 32) != epoch)
        {
            return AssemblyVersion;
        }

        try
        {
            await engine.WaitForAssembliesAsync(version & uint.MaxValue, linked.Token).ConfigureAwait(false);
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
        var (engine, epoch, cancellation) = CaptureRuntime(cancellationToken);
        using var linked = cancellation;
        var reply = await engine.CompleteAsync(request, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        return reply with { AssemblyVersion = (epoch << 32) | (reply.AssemblyVersion & uint.MaxValue) };
    }

    /// <inheritdoc />
    public async Task<AnalysisReply> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        var (engine, epoch, cancellation) = CaptureRuntime(cancellationToken);
        using var linked = cancellation;
        var reply = await engine.AnalyzeAsync(request, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        return reply with { AssemblyVersion = (epoch << 32) | (reply.AssemblyVersion & uint.MaxValue) };
    }

    /// <inheritdoc />
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken) =>
        HandleCoreAsync(line, null, cancellationToken);

    /// <inheritdoc />
    public Task<HandleReply> HandleSourceAsync(string line, AnalysisLocation location, CancellationToken cancellationToken) =>
        HandleCoreAsync(line, location, cancellationToken);

    /// <summary>
    /// Handles the first of several retained lines, taking the ordinary instructions that follow it in the same host call.
    /// </summary>
    /// <param name="lines">Consecutive lines starting with the one to handle. <see cref="PendingInput"/> holds what follows it.</param>
    /// <param name="locations">Their locations in the submitting document.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A reply for each line handled, always including the first.</returns>
    public async Task<HandleReply[]> HandleSourceRunAsync(
        string[] lines,
        AnalysisLocation[] locations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(locations);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (lines.Length == 0 || lines.Length != locations.Length)
        {
            throw new ArgumentException("Every line needs its location.", nameof(locations));
        }

        if (lines.Length > 1 && _engine is IHostedEngine)
        {
            var replies = await HandleRetainedRunAsync(lines, locations, cancellationToken).ConfigureAwait(false);
            if (replies.Length != 0)
            {
                return replies;
            }
        }

        return [await HandleCoreAsync(lines[0], locations[0], cancellationToken).ConfigureAwait(false)];
    }

    private async Task<HandleReply[]> HandleRetainedRunAsync(
        string[] lines,
        AnalysisLocation[] locations,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (RuntimeState != SessionRuntimeState.Ready || _engine is not IHostedEngine hosted)
            {
                return [];
            }

            var count = RetainedRun(lines);
            if (count < 2)
            {
                return [];
            }

            var replies = await hosted.HandleRetainedSourceRunAsync(lines[..count], locations[..count], linked.Token)
                .ConfigureAwait(false);
            if (replies.Length == 0)
            {
                return replies;
            }

            for (var index = 0; index < replies.Length; index++)
            {
                replies[index] = SuppressStreamedOutput(replies[index]);
            }

            // A refused line published the revision that holds it, so only what follows it is still unsent.
            var handled = replies.Length - 1;
            if (!replies[handled].Succeeded)
            {
                _checkpointPendingInput = PendingInput[handled..];
            }

            _checkpointBatch = replies[handled].Succeeded && PendingInput.Length > handled;
            return replies;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or ReplEngineException)
        {
            if (!RecoverHostFailures && exception is ReplEngineException { ExitCode: 3 })
            {
                throw;
            }

            _checkpointBatch = false;
            return [Failure(exception.Message) with { Quit = _disposed }];
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return [new HandleReply(false, true, [], Status)];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HandleReply> HandleCoreAsync(string line, AnalysisLocation? location, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SessionAction? frontend;
        try
        {
            frontend = FrontendAction(line);
        }
        catch (ArgumentException exception)
        {
            var failure = Failure(exception.Message);
            return failure with { Lines = [CommandEcho(line), .. failure.Lines] };
        }

        if (frontend?.Operation == SessionOperation.Restart && ExternalActionAsync is null)
        {
            var echo = CommandEcho(line);
            _checkpointPendingInput = PendingInput;
            var restarted = await RestartAsync(cancellationToken).ConfigureAwait(false);
            return RecoveryCompleted is null ? restarted.Reply with { Lines = [echo, .. restarted.Reply.Lines] }
                : restarted.Reply with { Lines = [], SessionEditor = null };
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        HandleReply? reply = null;
        try
        {
            if (frontend is not null)
            {
                reply = new HandleReply(true, false, [CommandEcho(line)], Status);
                BeginFrontendOutput(reply.Lines);
                var handled = await PerformAsync(new SessionRequest { Action = frontend, Editor = Editor }, cancellationToken)
                    .ConfigureAwait(false);
                handled = handled with { Reply = SuppressStreamedOutput(handled.Reply) };
                Workspace = handled;
                if (handled.Reply.SessionEditor is { } restored)
                {
                    Editor = restored;
                }

                await CheckpointAsync(cancellationToken).ConfigureAwait(false);
                return handled.Reply with { Lines = [.. UnstreamedFrontendOutput(reply.Lines), .. handled.Reply.Lines] };
            }

            if (RuntimeState != SessionRuntimeState.Ready)
            {
                if (line.Trim() is ".help" or ".h" or "?")
                {
                    var help = LocalHelp();
                    return help with { Lines = [CommandEcho(line), .. help.Lines] };
                }

                return Failure("host unavailable; keep editing, save with .session save, or use .session restart");
            }

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

            if (location is not null && !boundary && _engine is IHostedEngine hosted && CanDeferCheckpoint(line))
            {
                reply = await hosted.HandleRetainedSourceAsync(line, location, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                reply = location is null ? await _engine.HandleAsync(line, cancellationToken).ConfigureAwait(false)
                    : await _engine.HandleSourceAsync(line, location, cancellationToken).ConfigureAwait(false);
            }
            // Hosted input remains paired with the source revision that actually acknowledged it.
            if (_engine is not IHostedEngine)
            {
                _checkpointPendingInput = PendingInput;
            }

            reply = SuppressStreamedOutput(reply);
            if (reply.AssemblyExport is { } export && ExportAsync is { } exportAssembly)
            {
                await exportAssembly(export, cancellationToken).ConfigureAwait(false);
                reply = reply with { AssemblyExport = null };
            }

            if (reply.SessionAction is { } action)
            {
                BeginFrontendOutput(reply.Lines);
                var result = await PerformAsync(new SessionRequest { Action = action, Editor = Editor }, cancellationToken)
                    .ConfigureAwait(false);
                result = result with { Reply = SuppressStreamedOutput(result.Reply) };
                Workspace = result;
                reply = result.Reply with
                {
                    Lines = [.. UnstreamedFrontendOutput(reply.Lines), .. result.Reply.Lines], SessionAction = null,
                };
            }
            else if (reply.Quit && !await CanLeaveAsync(quitting: true, force: false, cancellationToken).ConfigureAwait(false))
            {
                reply = reply with { Quit = false };
            }

            if (reply.SessionEditor is { } editor)
            {
                Editor = editor;
            }

            _checkpointBatch = (PublishCheckpointAsync is not null || _engine is IHostedEngine)
                && PendingInput.Length != 0 && reply.Succeeded
                && reply.SessionEditor is null && !reply.Quit;
            if (!_checkpointBatch || boundary)
            {
                await CheckpointAsync(cancellationToken).ConfigureAwait(false);
            }

            return reply;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or ReplEngineException)
        {
            if (!RecoverHostFailures && exception is ReplEngineException { ExitCode: 3 })
            {
                throw;
            }

            _checkpointBatch = false;
            var failed = Failure(exception.Message);
            return failed with { Quit = _disposed, Lines = [.. UnstreamedFrontendOutput(reply?.Lines ?? []), .. failed.Lines],
                Diagnostics = reply?.Diagnostics ?? [] };
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return new HandleReply(false, true, UnstreamedFrontendOutput(reply?.Lines ?? []), Status);
        }
        finally
        {
            EndFrontendOutput();
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SessionReply> SessionAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.Action.Operation == SessionOperation.Restart && ExternalActionAsync is null)
        {
            Editor = request.Editor;
            return await RestartAsync(cancellationToken).ConfigureAwait(false);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Editor = request.Editor;
            var result = await PerformAsync(request, cancellationToken).ConfigureAwait(false);
            result = result with { Reply = SuppressStreamedOutput(result.Reply) };
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
        request = request with { HistoryLineLimit = request.HistoryLineLimit ?? HistoryLineLimit };
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

        if (RuntimeState != SessionRuntimeState.Ready)
        {
            return await PerformOfflineAsync(request, cancellationToken).ConfigureAwait(false);
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
            await InstallEngineAsync(candidate).ConfigureAwait(false);
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
        if (RuntimeState != SessionRuntimeState.Ready)
        {
            var retained = Workspace ?? new SessionReply();
            return retained with
            {
                Dirty = retained.Dirty || !SameEditorText(retained.Document.Editor, Editor),
                Document = retained.Document with { Editor = Editor },
            };
        }

        Workspace = await _engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture },
            Editor = Editor,
        }, cancellationToken).ConfigureAwait(false);
        return Workspace;
    }

    private async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        if (_engine is IHostedEngine && PublishCheckpointAsync is null)
        {
            return;
        }

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

    private bool CanDeferCheckpoint(string line)
    {
        if (!_checkpointBatch || PendingInput.Length == 0 || Status.OpenMethod is null)
        {
            return false;
        }

        var retained = _checkpointPendingInput;
        var consumed = retained.Length - PendingInput.Length;
        if (consumed < 1 || retained[consumed - 1] != line
            || !retained.AsSpan(consumed).SequenceEqual(PendingInput))
        {
            return false;
        }

        // End the batch before comments, declarations, labels, commands, or closing braces can change its interpretation.
        var comment = Status.Mark.InBlockComment;
        return Vocabulary.IsInstruction(line, ref comment) && Vocabulary.IsInstruction(PendingInput[0], ref comment);
    }

    private int RetainedRun(string[] lines)
    {
        if (RequiresCheckpoint(lines[0]) || !CanDeferCheckpoint(lines[0]))
        {
            return 0;
        }

        // A line joins the run while it and the line after it are ordinary instructions in the retained tail.
        var pending = PendingInput;
        var comment = Status.Mark.InBlockComment;
        var count = 0;
        while (count < lines.Length && count < pending.Length && (count == 0 || lines[count] == pending[count - 1])
            && !RequiresCheckpoint(lines[count]) && Vocabulary.IsInstruction(lines[count], ref comment))
        {
            var following = comment;
            if (!Vocabulary.IsInstruction(pending[count], ref following))
            {
                break;
            }

            count++;
        }

        return count;
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
            // Withdrawn source no longer matches the retained tail, so the next accepted line must checkpoint.
            _checkpointBatch = false;
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
        await Initialization.ConfigureAwait(false);
        _startupCancellation?.Dispose();
        Task<SessionReply>? restart;
        lock (_lifecycleLock)
        {
            restart = _restartTask;
        }

        if (restart is not null)
        {
            try
            {
                await restart.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

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
