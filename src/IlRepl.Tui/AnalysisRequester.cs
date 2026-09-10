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
                var task = SendAsync(state, key, cancellation.Token);
                _pending = new PendingAnalysis(key, cancellation, task);
                _owned.Add(_pending);
            }
        }

        while (_completed.TryDequeue(out var completed))
        {
            if (completed.Key != _current)
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
    /// Stops publication and waits until all owned workers have released their snapshots.
    /// </summary>
    /// <returns>The completion of every outstanding request.</returns>
    public async Task SettleAsync()
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

        await Task.WhenAll(_owned.Select(work => work.Task)).ConfigureAwait(false);
        foreach (var work in _owned)
        {
            work.Cancellation.Dispose();
        }

        _owned.Clear();
        _completed.Clear();
    }

    private async Task SendAsync(PromptState state, AnalysisRequestKey key, CancellationToken cancellationToken)
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

        _completed.Enqueue(new CompletedAnalysis(key, reply));
        state.Invalidate?.Invoke();
    }
}
