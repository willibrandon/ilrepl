using System.Collections.Concurrent;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Publishes only analysis that still belongs to the editor's document and session.
/// </summary>
public sealed class AnalysisRequester(IReplEngine engine)
{
    private readonly IReplEngine _engine = engine;
    private readonly List<PendingAnalysis> _owned = [];
    private readonly ConcurrentQueue<CompletedAnalysis> _completed = new();
    private AnalysisRequestKey? _current;
    private PendingAnalysis? _pending;
    private Task? _settlement;
    private long _nextRequestId;
    private bool _stopped;

    /// <summary>
    /// Whether the current document is still being analyzed.
    /// </summary>
    public bool IsPending => _pending is not null;

    /// <summary>
    /// Applies completed work and starts analysis when the document or session changes.
    /// </summary>
    /// <param name="state">The prompt on the render thread.</param>
    public void Refresh(PromptState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_stopped)
        {
            return;
        }

        foreach (var work in _owned.Where(work => work.Task.IsCompleted).ToArray())
        {
            work.Cancellation.Dispose();
            _owned.Remove(work);
        }

        var key = new AnalysisRequestKey(state.Text, state.Editor.Document.Version, state.CaretLine - 1, state.CaretColumn,
            _engine.Status.Revision, _engine.AssemblyVersion);
        if (state.Busy)
        {
            Cancel();
            state.Analysis = null;
            state.PendingDiagnostic = null;
            state.Highlighter.Diagnostics = [];
            return;
        }

        if (key != _current)
        {
            var display = key.Text.Length > 0 && _current is { } previous
                && previous.Revision == key.Revision && previous.AssemblyVersion == key.AssemblyVersion
                    ? PromptDiagnostics.Display(state) : null;
            Cancel();
            _current = key;
            state.Analysis = null;
            state.PendingDiagnostic = display;
            state.Highlighter.Diagnostics = [];
            if (key.Text.Length > 0)
            {
                var cancellation = new CancellationTokenSource();
                var id = ++_nextRequestId;
                var task = SendAsync(state, id, key, cancellation.Token);
                _pending = new PendingAnalysis(id, key, cancellation, task);
                _owned.Add(_pending);
            }
        }

        while (_completed.TryDequeue(out var completed))
        {
            if (completed.Key != _current || completed.Id != _pending?.Id)
            {
                continue;
            }

            _pending = null;
            state.PendingDiagnostic = null;
            if (completed.Reply is { } reply && reply.Revision == key.Revision && reply.AssemblyVersion == key.AssemblyVersion
                && reply.DocumentVersion == key.Version)
            {
                state.Analysis = reply;
                state.Highlighter.Diagnostics = reply.Diagnostics;
            }
        }
    }

    /// <summary>
    /// Cancels work for a document that is about to be submitted or replaced.
    /// </summary>
    public void Cancel()
    {
        if (_pending is { } pending && !pending.Task.IsCompleted)
        {
            pending.Cancellation.Cancel();
        }

        _pending = null;
        _current = null;
    }

    /// <summary>
    /// Stops publication and waits for workers, closing the engine when cancellation does not settle in time.
    /// </summary>
    /// <param name="timeout">The maximum wait before closing a stalled engine.</param>
    /// <returns>The completion of every outstanding request.</returns>
    public Task SettleAsync(TimeSpan timeout) => _settlement ??= SettleCoreAsync(timeout);

    private async Task SettleCoreAsync(TimeSpan timeout)
    {
        _stopped = true;
        Cancel();
        foreach (var work in _owned)
        {
            if (!work.Task.IsCompleted)
            {
                await work.Cancellation.CancelAsync().ConfigureAwait(false);
            }
        }

        var joined = Task.WhenAll(_owned.Select(work => work.Task));
        try
        {
            await joined.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _engine.DisposeAsync().AsTask().WaitAsync(timeout).ConfigureAwait(false);
            await joined.WaitAsync(timeout).ConfigureAwait(false);
        }

        foreach (var work in _owned)
        {
            work.Cancellation.Dispose();
        }

        _owned.Clear();
        _completed.Clear();
    }

    private async Task SendAsync(PromptState state, long id, AnalysisRequestKey key, CancellationToken cancellationToken)
    {
        AnalysisReply? reply = null;
        try
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            reply = await _engine.AnalyzeAsync(new AnalysisRequest(key.Text.Split('\n'), key.Line, key.Caret, key.Version),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            reply = new AnalysisReply(key.Version, key.Revision, 0, key.AssemblyVersion,
                new AnalyzedStack(AnalyzedStackKind.Unknown, []), false,
                [new AnalysisDiagnostic("ANALYSIS001", AnalysisDiagnosticKind.Unknown, "analysis unavailable: " + exception.Message,
                    new AnalysisLocation("document", key.Line, 0, 0), [])]);
        }

        _completed.Enqueue(new CompletedAnalysis(id, key, reply));
        state.Invalidate?.Invoke();
    }
}
