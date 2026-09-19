namespace IlRepl.Protocol;

/// <summary>
/// Cancels explicit replay while preserving the complete experiment in a fresh inactive runtime.
/// </summary>
public sealed partial class SessionController
{
    private readonly Lock _executionLock = new();
    private CancellationTokenSource? _executionCancellation;
    private IReplEngine? _runningCandidate;
    private SessionReply? _runningCheckpoint;
    private HostExit? _runningExit;

    /// <summary>
    /// Whether an explicit replay owns a disposable candidate runtime.
    /// </summary>
    public bool IsReplaying
    {
        get
        {
            lock (_executionLock)
            {
                return _executionCancellation is not null;
            }
        }
    }

    /// <summary>
    /// Interrupts an explicit session run without withdrawing previously accepted source.
    /// </summary>
    public void CancelExecution()
    {
        lock (_executionLock)
        {
            _executionCancellation?.Cancel();
        }
    }

    private void ObserveRunningCandidate(IReplEngine candidate)
    {
        if (candidate is IInterruptibleEngine interruptible)
        {
            interruptible.ProgressChanged += progress =>
            {
                if (ReferenceEquals(candidate, _runningCandidate))
                {
                    ProgressChanged?.Invoke(progress);
                }
            };
        }

        if (candidate is not IHostedEngine hosted)
        {
            return;
        }

        hosted.OutputReceived += output =>
        {
            if (ReferenceEquals(candidate, _runningCandidate))
            {
                ForwardOutput(output);
            }
        };

        hosted.CheckpointReceived += checkpoint =>
        {
            if (ReferenceEquals(candidate, _runningCandidate))
            {
                _runningCheckpoint = checkpoint;
            }
        };

        hosted.Exited += exit =>
        {
            if (ReferenceEquals(candidate, _runningCandidate) && !exit.Expected)
            {
                _runningExit = exit;
                lock (_lifecycleLock)
                {
                    _lastHostExit = exit;
                }
            }
        };
    }

    private async Task<(IReplEngine Engine, SessionReply Reply)> RunCandidateAsync(
        IReplEngine candidate,
        SessionRequest request,
        CancellationToken cancellationToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_executionLock)
        {
            _executionCancellation = execution;
            _runningCandidate = candidate;
        }

        _runningCheckpoint = null;
        _runningExit = null;
        ObserveRunningCandidate(candidate);
        try
        {
            return (candidate, await candidate.SessionAsync(request, execution.Token).WaitAsync(execution.Token)
                .ConfigureAwait(false));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && !_disposed
            && (exception is OperationCanceledException && execution.IsCancellationRequested
                || exception is ReplEngineException { ExitCode: 3 } && RecoverHostFailures && _runningExit is not null))
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
            var source = request.Document!;
            var checkpoint = _runningCheckpoint;
            var interruptedNumber = checkpoint?.PendingSubmission;
            var interruption = new SessionInterruption
            {
                Number = interruptedNumber ?? (request.Action.Numbers.Length != 0 ? request.Action.Numbers.Min()
                    : source.Cells.FirstOrDefault(cell => cell.Kind == "cell")?.Number ?? Status.CellNumber),
                Source = [".session run" + (request.Action.Numbers.Length == 0 ? "" : " "
                    + string.Join(' ', request.Action.Numbers.Order()))],
                ExitCode = _runningExit?.ExitCode, StandardError = _runningExit?.StandardError,
            };

            if (interruptedNumber is { } number)
            {
                var cell = source.Cells.FirstOrDefault(item => item.Number == number) ?? new SessionCell { Number = number };
                cell = cell with { State = "interrupted", Source = checkpoint!.PendingSource, Inputs = checkpoint.PendingInputs };
                source = source with
                {
                    Cells = source.Cells.Any(item => item.Number == number)
                        ? [.. source.Cells.Select(item => item.Number == number ? cell : item)]
                        : [.. source.Cells, cell],
                };
            }

            source = source with { Interruptions = [.. source.Interruptions, interruption] };
            IReplEngine? recovered = null;
            try
            {
                recovered = await _start(cancellationToken).ConfigureAwait(false);
                if (!Status.Mark.EchoStack)
                {
                    await recovered.HandleAsync(".quiet on", cancellationToken).ConfigureAwait(false);
                }

                if (Status.Mark.ShowTiming)
                {
                    await recovered.HandleAsync(".time on", cancellationToken).ConfigureAwait(false);
                }

                var reply = await recovered.SessionAsync(new SessionRequest
                {
                    Action = new SessionAction { Operation = SessionOperation.Hydrate, Path = request.Action.Path },
                    Document = source,
                    Editor = source.Editor, Modified = true,
                    HistoryLineLimit = request.HistoryLineLimit ?? HistoryLineLimit,
                }, cancellationToken).ConfigureAwait(false);

                var notice = _runningExit is null ? "session run cancelled" : "session run interrupted by host exit";
                return (recovered, reply with { Reply = reply.Reply with { Succeeded = false,
                    Lines = [.. ExitLines(_runningExit), .. reply.Reply.Lines,
                    TranscriptLine.Of(LineKind.Info, "  " + notice + "; all source remains available", SpanStyle.Dim)] } });
            }
            catch (Exception recoveryFailure)
            {
                if (recovered is not null)
                {
                    await recovered.DisposeAsync().ConfigureAwait(false);
                }

                if (recoveryFailure is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                SetRuntimeState(SessionRuntimeState.Unavailable);
                return (new InactiveEngine(), new SessionReply
                {
                    Document = source, Path = request.Action.Path, Dirty = true,
                    Reply = Failure("host unavailable: " + recoveryFailure.Message
                        + "; source remains editable; use .session save or .session restart") with
                    {
                        SessionEditor = source.Editor,
                    },
                });
            }
        }
        finally
        {
            _runningCheckpoint = null;
            _runningExit = null;
            lock (_executionLock)
            {
                _runningCandidate = null;
                _executionCancellation = null;
            }

            ProgressChanged?.Invoke(Progress);
        }
    }
}
