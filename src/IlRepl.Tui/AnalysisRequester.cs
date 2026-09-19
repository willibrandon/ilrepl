using System.Collections.Concurrent;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Coalesces document analysis into one active request and one replaceable pending document.
/// </summary>
public sealed class AnalysisRequester(IReplEngine engine)
{
    private readonly IReplEngine _engine = engine;
    private readonly ConcurrentQueue<CompletedAnalysis> _completed = new();
    private AnalysisRequestKey? _current;
    private AnalysisRequestKey? _desired;
    private (AnalysisRequestKey Key, AnalysisReply Reply)? _published;
    private PendingAnalysis? _active;
    private Task? _settlement;
    private long _nextRequestId;
    private long _minimumRequestId;
    private bool _stopped;

    /// <summary>
    /// Whether the current document is waiting for a result or obsolete work to settle.
    /// </summary>
    public bool IsPending => _desired is not null;

    /// <summary>
    /// Returns diagnostics while their document and session remain current, including during caret-only refreshes.
    /// </summary>
    internal IReadOnlyList<AnalysisDiagnostic> Diagnostics(PromptState state)
    {
        if (_published is { } published && published.Key.Text == state.Text
            && published.Key.Version == state.Editor.Document.Version && published.Key.Revision == _engine.Status.Revision
            && published.Key.AssemblyVersion == _engine.AssemblyVersion)
        {
            return published.Reply.Diagnostics;
        }

        return [];
    }

    /// <summary>
    /// Keeps published source locations usable across catalog refreshes without carrying them into changed source or runtimes.
    /// </summary>
    internal IReadOnlyList<AnalysisDiagnostic> NavigationDiagnostics(PromptState state)
    {
        if (_published is { } published && published.Key.Text == state.Text
            && published.Key.Version == state.Editor.Document.Version && published.Key.Revision == _engine.Status.Revision
            && (_engine is not SessionController || published.Key.AssemblyVersion >> 32 == _engine.AssemblyVersion >> 32))
        {
            return published.Reply.Diagnostics;
        }

        return [];
    }

    /// <summary>
    /// Applies completed work and coalesces source changes once per render while projecting caret moves locally.
    /// </summary>
    /// <param name="state">The prompt on the render thread.</param>
    public void Refresh(PromptState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_stopped)
        {
            return;
        }

        if (_active is { Task.IsCompleted: true } settled)
        {
            settled.Cancellation.Dispose();
            _active = null;
        }

        var key = new AnalysisRequestKey(state.Text, state.Editor.Document.Version, state.CaretLine - 1, state.CaretColumn,
            _engine.Status.Revision, _engine.AssemblyVersion);
        if (key != _current)
        {
            var sameDocument = _current?.SameDocument(key) == true;
            if (!sameDocument)
            {
                _minimumRequestId = ++_nextRequestId;
                var display = key.Text.Length > 0 && _current is { } previous
                    && previous.Revision == key.Revision && previous.AssemblyVersion == key.AssemblyVersion
                        ? PromptDiagnostics.PreviousDisplay(state) : null;
                _active?.Cancellation.Cancel();
                state.Analysis = null;
                state.PendingDiagnostic = display;
                state.Highlighter.Diagnostics = [];
            }

            _current = key;
            _desired = key.Text.Length == 0 ? null : key;
        }

        while (_completed.TryDequeue(out var completed))
        {
            if (completed.Id < _minimumRequestId || !completed.Key.SameDocument(key))
            {
                continue;
            }

            state.PendingDiagnostic = null;
            if (completed.Reply is { } reply && reply.Revision == key.Revision && reply.AssemblyVersion == key.AssemblyVersion
                && reply.DocumentVersion == key.Version)
            {
                _published = (completed.Key, reply);
            }
            else if (_active?.Id == completed.Id || _active is null)
            {
                _desired = null;
            }
        }

        if (_published is { } published && published.Key.SameDocument(key)
            && (published.Reply.Positions.Count > 0 || published.Key.Line == key.Line))
        {
            state.Analysis = published.Reply.At(key.Line);
            state.PendingDiagnostic = null;
            _desired = null;
        }

        if (_desired is { } desired && _active is null)
        {
            var cancellation = new CancellationTokenSource();
            var id = ++_nextRequestId;
            var task = SendAsync(state, id, desired, cancellation.Token);
            _active = new PendingAnalysis(id, desired, cancellation, task);
        }
    }

    /// <summary>
    /// Cancels active analysis and discards the pending document before submission or replacement.
    /// </summary>
    public void Cancel()
    {
        _active?.Cancellation.Cancel();
        _minimumRequestId = ++_nextRequestId;
        _desired = null;
        _current = null;
        _published = null;
    }

    /// <summary>
    /// Stops publication and waits for the active request, closing the engine only when shutdown cannot settle.
    /// </summary>
    /// <param name="timeout">The maximum wait before closing a stalled engine.</param>
    /// <returns>The completion of every outstanding request.</returns>
    public Task SettleAsync(TimeSpan timeout) => _settlement ??= SettleCoreAsync(timeout);

    private async Task SettleCoreAsync(TimeSpan timeout)
    {
        _stopped = true;
        Cancel();
        if (_active is { } active)
        {
            try
            {
                await active.Task.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await _engine.DisposeAsync().AsTask().WaitAsync(timeout).ConfigureAwait(false);
                await active.Task.WaitAsync(timeout).ConfigureAwait(false);
            }

            active.Cancellation.Dispose();
            _active = null;
        }

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
