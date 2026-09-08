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
    private readonly List<SubmissionUnit> _units;
    private readonly IReadOnlyList<string> _commands;
    private readonly int _openDepth;
    private readonly bool _inBlockComment;
    private readonly Func<CancellationToken, Task> _persist;
    private readonly Action<SubmissionEvent> _post;
    private volatile bool _cancelled;
    private volatile bool _running = true;
    private int _sent;
    private int _boundary;
    private SessionMark? _mark;
    private bool _provisional;

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
        _commands = engine.Vocabulary.Commands;
        _openDepth = openDepth;
        _inBlockComment = inBlockComment;
        _units = [.. SubmissionSplitter.Split(lines, openDepth, inBlockComment, _commands)];
        _persist = persist;
        _post = post;
        Total = _units.Sum(u => u.Sends.Count);
        var after = BlockBalance.Scan(string.Join('\n', lines), openDepth, inBlockComment, commands: engine.Vocabulary.Commands);
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
    public int Total { get; private set; }

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
            // What the text says the engine's depth should be after each line, so a command
            // that ended a block, .clear or .reset among them, is noticed when it has.
            var expect = new BlockScan(_openDepth, _inBlockComment, false);
            for (var u = 0; u < _units.Count; u++)
            {
                var unit = _units[u];
                var mark = _engine.Status.Mark;
                var restart = unit.Start;
                var moved = false;
                var provisional = unit.Kind == SubmissionUnitKind.Block;
                _boundary = restart;
                _mark = mark;
                _provisional = provisional;
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
                    expect = BlockBalance.Scan(_lines[index], expect.Depth, expect.InBlockComment, expect.AwaitingBrace, _commands);
                    var cut = false;
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

                            _boundary = i < unit.Sends.Count - 1 ? restart : unit.End;
                            _mark = mark;
                            _provisional = provisional;
                            if (index < _lines.Count - 1 && Diverged(reply.Status, expect))
                            {
                                // The engine is no longer where the text was written for: a
                                // command ended a block. The lines after it are cut again from
                                // where the engine now is, so a blank line among them runs the
                                // cell and a brace closes what is open now.
                                Recut(u, i, index, reply.Status);
                                unit = _units[u];
                                expect = new BlockScan(reply.Status.OpenDepth, reply.Status.Mark.InBlockComment, false);
                                cut = true;
                            }

                            break;
                        case SubmissionOutcome.Refused:
                        {
                            var withdrawn = await WithdrawAsync(mark, provisional).ConfigureAwait(false);
                            var note = moved ? $"the lines before '{_lines[restart - 1].Trim()}' stayed applied" : null;
                            if (unit.Kind == SubmissionUnitKind.Block)
                            {
                                // A block comes back whole with the refused line selected.
                                _post(SubmissionEvent.Refused([.. reply.Lines, .. withdrawn], TextFrom(restart), Math.Max(0, index - restart), note));
                            }
                            else
                            {
                                // A line on its own is not put back: the error and history have
                                // it, as in any REPL. Only what never went comes back.
                                _post(SubmissionEvent.Refused([.. reply.Lines, .. withdrawn], TextFrom(index + 1), 0, note, select: false));
                            }

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

                    var last = u == _units.Count - 1 && i == unit.Sends.Count - 1;
                    _post(last ? SubmissionEvent.Done(reply.Lines) : SubmissionEvent.Reply(reply.Lines));
                    if (cut)
                    {
                        break;
                    }
                }
            }

            if (Total == 0)
            {
                _post(SubmissionEvent.Done([]));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever went wrong, the prompt must hear that the submission is over, and the
            // lines that had not gone by come back with the unit in flight withdrawn if it can be.
            IReadOnlyList<TranscriptLine> withdrawn = [];
            try
            {
                withdrawn = _mark is { } mark ? await WithdrawAsync(mark, _provisional).ConfigureAwait(false) : [];
            }
            catch (Exception withdrawal) when (withdrawal is not OperationCanceledException)
            {
                // The engine is past helping; the text is what matters.
            }

            _post(SubmissionEvent.Failure(withdrawn, ex.Message, TextFrom(_boundary)));
        }
        finally
        {
            _running = false;
        }
    }

    // The engine's open depth counts braces it has seen; the text counts a header waiting for
    // its brace as open as well.
    private static bool Diverged(SessionStatus status, BlockScan expect) =>
        status.OpenDepth != expect.Depth - (expect.AwaitingBrace ? 1 : 0) || status.Mark.InBlockComment != expect.InBlockComment;

    // Ends the unit at the line just sent and cuts the lines after it afresh from the engine's state.
    private void Recut(int u, int i, int index, SessionStatus status)
    {
        var unit = _units[u];
        var from = index + 1;
        var rest = SubmissionSplitter.Split([.. _lines.Skip(from)], status.OpenDepth, status.Mark.InBlockComment, _commands)
            .Select(r => new SubmissionUnit(r.Start + from, r.End + from, [.. r.Sends.Select(send => send + from)], r.Kind));
        _units[u] = new SubmissionUnit(unit.Start, from, [.. unit.Sends.Take(i + 1)], unit.Kind);
        _units.RemoveRange(u + 1, _units.Count - u - 1);
        _units.AddRange(rest);
        Total = _units.Sum(x => x.Sends.Count);
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
