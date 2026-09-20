namespace IlRepl.Protocol;

/// <summary>
/// Retains acknowledged source independently of host availability and coordinates explicit runtime replacement.
/// </summary>
public sealed partial class SessionController : IInterruptibleEngine
{
    private readonly Lock _lifecycleLock = new();
    private Task<SessionReply>? _restartTask;
    private SessionRuntimeState _runtimeState;
    private HostExit? _lastHostExit;
    private HostExit? _pendingHostExit;
    private string[] _checkpointPendingInput = [];
    private string[] _recoveredInput = [];

    /// <summary>
    /// Whether an unexpected host exit starts one attempt to reconstruct the retained source.
    /// </summary>
    public bool RecoverHostFailures { get; set; } = true;

    /// <summary>
    /// The execution runtime's availability, independent of source editing and storage.
    /// </summary>
    public SessionRuntimeState RuntimeState
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _runtimeState;
            }
        }
    }

    /// <summary>
    /// The last observed host termination, including its bounded diagnostic output.
    /// </summary>
    public HostExit? LastHostExit
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _lastHostExit;
            }
        }
    }

    /// <summary>
    /// Announces a change in host availability without waiting for an executing cell.
    /// </summary>
    public event Action<SessionRuntimeState>? RuntimeStateChanged;

    /// <summary>
    /// Publishes reconstructed source and notices after replacement has settled.
    /// </summary>
    public event Action<SessionReply>? RecoveryCompleted;

    /// <summary>
    /// Streams user output independently of a pending submission reply.
    /// </summary>
    public event Action<ExecutionOutput>? OutputReceived;

    /// <summary>
    /// The current execution phase, available independently of the controller's mutation gate.
    /// </summary>
    public ExecutionProgress Progress => (_runningCandidate ?? _engine) is IInterruptibleEngine interruptible
        ? interruptible.Progress : new ExecutionProgress("", 0, ExecutionPhase.Cooperative, false);

    /// <summary>
    /// Announces execution phase changes for the currently installed runtime.
    /// </summary>
    public event Action<ExecutionProgress>? ProgressChanged;

    /// <summary>
    /// Interrupts only the identified operation, preserving source when explicit replay replaces its runtime.
    /// </summary>
    /// <param name="identity">The operation observed by the frontend.</param>
    /// <param name="cancellationToken">Cancels delivery of the request.</param>
    /// <returns>Whether that operation was still active.</returns>
    public async Task<bool> InterruptAsync(string identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IInterruptibleEngine? interruptible;
        Task? replayCancellation = null;
        lock (_executionLock)
        {
            interruptible = (_runningCandidate ?? _engine) as IInterruptibleEngine;
            if (_runningCandidate is not null)
            {
                if (interruptible?.Progress is not { IsRunning: true } progress || progress.Identity != identity)
                {
                    return false;
                }

                replayCancellation = _executionCancellation?.CancelAsync();
            }
        }

        if (replayCancellation is not null)
        {
            await replayCancellation.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        return interruptible is not null && await interruptible.InterruptAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces a runtime only if the operation and phase shown in an escalation notice remain active.
    /// </summary>
    /// <param name="identity">The operation acknowledged by the user.</param>
    /// <param name="sequence">The exact displayed phase revision.</param>
    /// <param name="cancellationToken">Cancels the caller's wait.</param>
    /// <returns>Whether explicit escalation still applied to that operation.</returns>
    public async Task<bool> EscalateAsync(string identity, long sequence, CancellationToken cancellationToken)
    {
        Task<SessionReply> restart;
        lock (_lifecycleLock)
        {
            var progress = Progress;
            if (_runtimeState != SessionRuntimeState.Ready || !progress.IsRunning || !progress.CancellationRequested
                || progress.Identity != identity || progress.Sequence != sequence)
            {
                return false;
            }

            restart = BeginRestartLocked();
        }

        await restart.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Stops the existing runtime and reconstructs acknowledged source without replaying cells.
    /// </summary>
    /// <param name="cancellationToken">Cancels the caller's wait without abandoning process cleanup.</param>
    /// <returns>The retained workspace and replacement diagnostics.</returns>
    public Task<SessionReply> RestartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            return BeginRestartLocked().WaitAsync(cancellationToken);
        }
    }

    private Task<SessionReply> BeginRestartLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_restartTask is { IsCompleted: false })
        {
            return _restartTask;
        }

        _runtimeState = SessionRuntimeState.Restarting;
        CancelStartup();
        var previous = _engine;
        var exit = _pendingHostExit;
        _pendingHostExit = null;
        _restartTask = Task.Run(() => RestartCoreAsync(previous, exit, _lifetime.Token), CancellationToken.None);
        return _restartTask;
    }

    private void ObserveEngine(IReplEngine engine)
    {
        ObserveSupervision(engine);
        if (engine is IInterruptibleEngine interruptible)
        {
            interruptible.ProgressChanged += progress =>
            {
                if (ReferenceEquals(engine, _engine))
                {
                    ProgressChanged?.Invoke(progress);
                }
            };
        }

        if (engine is not IHostedEngine hosted)
        {
            return;
        }

        hosted.CheckpointReceived += checkpoint =>
        {
            if (!ReferenceEquals(engine, _engine))
            {
                return;
            }

            _checkpointPendingInput = PendingInput;
            var current = Workspace;
            Workspace = checkpoint with
            {
                Path = checkpoint.Path ?? current?.Path,
                Dirty = checkpoint.Dirty || current?.Dirty == true,
                Document = checkpoint.Document with { Editor = Editor },
            };
        };

        hosted.OutputReceived += output =>
        {
            if (ReferenceEquals(engine, _engine))
            {
                ForwardOutput(output);
            }
        };

        hosted.Exited += exit =>
        {
            lock (_lifecycleLock)
            {
                if (_disposed || !ReferenceEquals(engine, _engine) || exit.Expected)
                {
                    return;
                }

                _lastHostExit = exit;
                _pendingHostExit = exit;
                if (RecoverHostFailures)
                {
                    _ = BeginRestartLocked();
                }
                else
                {
                    _runtimeState = SessionRuntimeState.Unavailable;
                }
            }

            RuntimeStateChanged?.Invoke(RuntimeState);
        };
    }

    private async Task<SessionReply> RestartCoreAsync(IReplEngine previous, HostExit? exit, CancellationToken cancellationToken)
    {
        try
        {
            return await RestartRuntimeAsync(previous, exit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var retained = InterruptedWorkspace(Workspace ?? new SessionReply(), exit);
            var result = retained with
            {
                Reply = Failure("host unavailable: " + exception.Message
                    + "; source remains editable; use .session save or .session restart") with
                {
                    SessionEditor = retained.Document.Editor,
                },
            };

            Editor = result.Document.Editor;
            _checkpointPendingInput = [];
            QueuedInput = [];
            Workspace = result;
            SetRuntimeState(SessionRuntimeState.Unavailable);
            RecoveryCompleted?.Invoke(result with { RecoveredInput = _recoveredInput });
            return result;
        }
    }

    private async Task<SessionReply> RestartRuntimeAsync(IReplEngine previous, HostExit? exit, CancellationToken cancellationToken)
    {
        RuntimeStateChanged?.Invoke(SessionRuntimeState.Restarting);
        var presentation = previous.Status.Mark;
        await Initialization.ConfigureAwait(false);
        // Closing a direct connection releases an in-flight RPC even when user IL never returns.
        if (previous is IHostedEngine hosted)
        {
            await hosted.TerminateAsync(cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SessionReply result;
        try
        {
            var retained = Workspace ?? new SessionReply();
            if (previous is not IHostedEngine && previous is not InactiveEngine)
            {
                retained = await previous.SessionAsync(new SessionRequest
                {
                    Action = new SessionAction { Operation = SessionOperation.Capture }, Editor = Editor,
                }, cancellationToken).ConfigureAwait(false);
            }

            retained = InterruptedWorkspace(retained, exit);
            Workspace = retained;
            await previous.DisposeAsync().ConfigureAwait(false);
            IReplEngine? candidate = null;
            try
            {
                candidate = await _start(cancellationToken).ConfigureAwait(false);
                await candidate.HandleAsync(presentation.EchoStack ? ".quiet off" : ".quiet on", cancellationToken)
                    .ConfigureAwait(false);
                await candidate.HandleAsync(presentation.ShowTiming ? ".time on" : ".time off", cancellationToken)
                    .ConfigureAwait(false);
                var restored = await candidate.SessionAsync(new SessionRequest
                {
                    Action = new SessionAction { Operation = SessionOperation.Hydrate, Path = retained.Path },
                    Document = retained.Document, Editor = retained.Document.Editor, Modified = retained.Dirty,
                    HistoryLineLimit = HistoryLineLimit,
                }, cancellationToken).ConfigureAwait(false);

                await InstallEngineAsync(candidate).ConfigureAwait(false);
                var editor = RecoveryEditor();
                result = restored with
                {
                    Document = restored.Document with { Editor = editor },
                    Reply = restored.Reply with
                    {
                        Lines = [.. ExitLines(exit),
                            TranscriptLine.Of(LineKind.Info,
                                "  runtime restarted; source and definitions retained, objects and static values reset", SpanStyle.Dim)],
                        SessionEditor = editor,
                    },
                };

                SetRuntimeState(SessionRuntimeState.Ready);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (candidate is not null)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }

                result = retained with
                {
                    Reply = Failure("host unavailable: " + exception.Message
                        + "; source remains editable; use .session save or .session restart") with
                        { SessionEditor = retained.Document.Editor },
                };

                SetRuntimeState(SessionRuntimeState.Unavailable);
            }

            Editor = result.Document.Editor;
            _checkpointPendingInput = [];
            QueuedInput = [];
            Workspace = result;
        }
        finally
        {
            _gate.Release();
        }

        RecoveryCompleted?.Invoke(result with { RecoveredInput = _recoveredInput });
        return result;
    }

    private SessionReply InterruptedWorkspace(SessionReply retained, HostExit? exit)
    {
        var editor = RecoveryEditor();
        var document = retained.Document with { Editor = editor };
        if (retained.PendingSubmission is { } number)
        {
            var source = retained.PendingSource;
            var interruption = new SessionInterruption
            {
                Number = number, Source = source, ExitCode = exit?.ExitCode, StandardError = exit?.StandardError,
            };

            var cells = document.Cells.Where(cell => cell.Number != number).ToArray();
            document = document with
            {
                Cells = [.. cells, new SessionCell
                {
                    Number = number, Source = source, Inputs = retained.PendingInputs, State = "interrupted",
                }],
                Interruptions = [.. document.Interruptions, interruption],
                Entries = [.. document.Entries, new SessionEntry { Number = number, Kind = SessionEntryKind.Run, Source = ["ret"] }],
            };
        }

        return retained with
        {
            Document = document,
            Dirty = retained.Dirty || retained.PendingSubmission is not null
                || !SameEditorText(retained.Document.Editor, editor),
            PendingSubmission = null, PendingSource = [], PendingInputs = [],
        };
    }

    private static bool SameEditorText(SessionEditor left, SessionEditor right) =>
        string.Join('\n', left.Lines) == string.Join('\n', right.Lines);

    private SessionEditor RecoveryEditor()
    {
        var editor = Editor;
        string[] prefix = [.. _checkpointPendingInput, .. QueuedInput];
        _recoveredInput = prefix;
        if (prefix.Length == 0)
        {
            return editor;
        }

        var offset = string.Join('\n', prefix).Length + (editor.Lines.Length == 0 ? 0 : 1);
        return editor with { Lines = [.. prefix, .. editor.Lines], Caret = editor.Caret + offset, Anchor = editor.Anchor + offset };
    }

    private static IEnumerable<TranscriptLine> ExitLines(HostExit? exit)
    {
        if (exit is null)
        {
            yield break;
        }

        var code = exit.ExitCode is { } value ? ExitCodes.Describe(value) : "unknown";
        yield return TranscriptLine.Of(LineKind.Error,
            $"  execution host exited (process {exit.ProcessId}, exit code {code})", SpanStyle.Error);
        if (exit.StandardError.Length != 0)
        {
            foreach (var line in exit.StandardError.TrimEnd().Split('\n'))
            {
                yield return TranscriptLine.Of(LineKind.Error, "  " + line.TrimEnd('\r'), SpanStyle.Error);
            }
        }
    }

    private async Task InstallEngineAsync(IReplEngine engine)
    {
        CancellationTokenSource cancellation;
        lock (_lifecycleLock)
        {
            cancellation = _replacement;
            _replacement = new CancellationTokenSource();
            _engine = engine;
            Interlocked.Increment(ref _epoch);
        }

        ObserveEngine(engine);
        if (engine is IInterruptibleEngine interruptible)
        {
            ProgressChanged?.Invoke(interruptible.Progress);
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        cancellation.Dispose();
    }

    private (IReplEngine Engine, long Epoch, CancellationTokenSource Cancellation) CaptureRuntime(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (_engine, _epoch,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _replacement.Token, _lifetime.Token));
        }
    }

    private void SetRuntimeState(SessionRuntimeState state)
    {
        lock (_lifecycleLock)
        {
            _runtimeState = state;
        }

        RuntimeStateChanged?.Invoke(state);
    }
}
