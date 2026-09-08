using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Sends a buffer to the engine one line at a time, off the render thread, and posts what
/// happened to the prompt's queue. Each reply is read against the mark its unit began with: a
/// refused line withdraws its unit and brings the text back; a line that ran or committed moves
/// the unit boundary past it; a cancel waits for the reply in flight and withdraws only what is
/// still provisional. The event that ends the submission carries the last reply's lines, so the
/// frame that shows them also sees the submission over. Between lines the worker yields, so the
/// screen repaints and keys are read even where the engine answers on the same thread.
/// </summary>
public sealed class Submission
{
    private readonly IReplEngine _engine;
    private readonly IReadOnlyList<string> _lines;
    private readonly IReadOnlyList<SubmissionUnit> _units;
    private readonly Func<CancellationToken, Task> _persist;
    private readonly Action<SubmissionEvent> _post;
    private volatile bool _cancelled;
    private volatile bool _running = true;
    private int _sent;

    /// <summary>
    /// Starts sending.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="lines">The buffer's lines.</param>
    /// <param name="openDepth">How many closing braces the engine is already waiting for.</param>
    /// <param name="inBlockComment">Whether the engine has a <c>/*</c> open when the buffer starts.</param>
    /// <param name="persist">Writes the history entry; awaited before the first line goes.</param>
    /// <param name="post">Where events go.</param>
    public Submission(IReplEngine engine, IReadOnlyList<string> lines, int openDepth, bool inBlockComment, Func<CancellationToken, Task> persist, Action<SubmissionEvent> post)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(post);
        _engine = engine;
        _lines = lines;
        _units = SubmissionSplitter.Split(lines, openDepth, inBlockComment);
        _persist = persist;
        _post = post;
        Total = _units.Sum(u => u.Sends.Count);
        var after = BlockBalance.Scan(string.Join('\n', lines), openDepth, inBlockComment);
        DepthAfter = Math.Max(0, after.Depth);
        CommentOpenAfter = after.InBlockComment;
        Completion = RunAsync();
    }

    /// <summary>
    /// The open depth the engine will be at once every line has gone by.
    /// </summary>
    public int DepthAfter { get; }

    /// <summary>
    /// Whether a block comment will still be open once every line has gone by.
    /// </summary>
    public bool CommentOpenAfter { get; }

    /// <summary>
    /// How many lines have been answered.
    /// </summary>
    public int Sent => Volatile.Read(ref _sent);

    /// <summary>
    /// How many lines there are to send.
    /// </summary>
    public int Total { get; }

    /// <summary>
    /// Whether lines are still going.
    /// </summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Completes when the last event has been posted.
    /// </summary>
    public Task Completion { get; }

    /// <summary>
    /// Stops after the reply in flight.
    /// </summary>
    /// <summary>
    /// True once a cancel has been asked for and the worker has not yet reached the check.
    /// </summary>
    public bool CancelRequested => _cancelled;

    /// <summary>
    /// Asks the worker to stop after the line in flight: a provisional unit is withdrawn and its
    /// text comes back; a unit that just committed stays.
    /// </summary>
    public void Cancel() => _cancelled = true;

    private async Task RunAsync()
    {
        try
        {
            await _persist(CancellationToken.None).ConfigureAwait(false);
            foreach (var unit in _units)
            {
                var mark = _engine.Status.Mark;
                var restart = unit.Start;
                var moved = false;
                var provisional = unit.Kind == SubmissionUnitKind.Block;
                for (var i = 0; i < unit.Sends.Count; i++)
                {
                    var index = unit.Sends[i];
                    if (Sent > 0)
                    {
                        // Between lines the worker steps off the thread, so the screen repaints and
                        // keys are read even where the engine answers on this very thread.
                        await Task.Yield();
                    }

                    if (_cancelled)
                    {
                        // A unit that has not started is neither withdrawn nor lost: it comes back
                        // whole, ahead of everything after it.
                        IReadOnlyList<TranscriptLine> withdrawn = i == 0 ? [] : await WithdrawAsync(mark, provisional).ConfigureAwait(false);
                        var from = i == 0 ? unit.Start : provisional ? restart : unit.End;
                        _post(SubmissionEvent.Cancel(withdrawn, TextFrom(from)));
                        return;
                    }

                    HandleReply reply;
                    try
                    {
                        reply = await _engine.HandleAsync(_lines[index], CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (ReplEngineException ex)
                    {
                        var withdrawn = await WithdrawAsync(mark, provisional).ConfigureAwait(false);
                        _post(SubmissionEvent.Failure(withdrawn, ex.Message, TextFrom(restart)));
                        return;
                    }

                    Interlocked.Increment(ref _sent);
                    var last = ReferenceEquals(unit, _units[^1]) && i == unit.Sends.Count - 1;
                    if (reply.Quit)
                    {
                        _post(SubmissionEvent.QuitRequested(reply.Lines));
                        return;
                    }

                    switch (SubmissionClassifier.Classify(mark, reply))
                    {
                        case SubmissionOutcome.Accepted:
                            break;
                        case SubmissionOutcome.Completed:
                            // A run, a commit, or a destructive command: nothing before it can be
                            // withdrawn now, so the unit starts over from the line after it.
                            mark = reply.Status.Mark;
                            provisional = false;
                            if (i < unit.Sends.Count - 1)
                            {
                                restart = index + 1;
                                moved = true;
                                provisional = true;
                            }

                            break;
                        case SubmissionOutcome.Refused:
                        {
                            var withdrawn = await WithdrawAsync(mark, provisional).ConfigureAwait(false);
                            var note = moved ? $"the lines before '{_lines[restart - 1].Trim()}' stayed applied" : null;
                            _post(SubmissionEvent.Refused([.. reply.Lines, .. withdrawn], TextFrom(restart), Math.Max(0, index - restart), note));
                            return;
                        }

                        case SubmissionOutcome.Failed:
                            // What ran stays run; the lines after it, a block's remainder included,
                            // come back so nothing typed is lost.
                            _post(SubmissionEvent.Failure(reply.Lines, null, TextFrom(index + 1)));
                            return;
                        default:
                            break;
                    }

                    _post(last ? SubmissionEvent.Done(reply.Lines) : SubmissionEvent.Reply(reply.Lines));
                }
            }

            if (Total == 0)
            {
                _post(SubmissionEvent.Done([]));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever went wrong, the prompt must hear that the submission is over.
            _post(SubmissionEvent.Failure([], ex.Message, ""));
        }
        finally
        {
            _running = false;
        }
    }

    private async Task<IReadOnlyList<TranscriptLine>> WithdrawAsync(SessionMark mark, bool provisional)
    {
        if (!provisional)
        {
            return [];
        }

        try
        {
            var back = await _engine.RollbackAsync(mark, CancellationToken.None).ConfigureAwait(false);
            return back.Lines;
        }
        catch (ReplEngineException)
        {
            // The engine is gone; there is nothing left to withdraw from.
            return [];
        }
    }

    private string TextFrom(int line) => line >= _lines.Count ? "" : string.Join('\n', _lines.Skip(line));
}
