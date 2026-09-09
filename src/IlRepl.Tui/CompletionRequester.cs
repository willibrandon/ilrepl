using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Owns completion requests, drops stale answers and settles every superseded task before an app can end.
/// </summary>
public sealed class CompletionRequester
{
    private readonly IReplEngine _engine;
    private readonly CaretClassifier _classifier;
    private readonly List<PendingCompletion> _owned = [];
    private PendingCompletion? _pending;
    private CompletionRequestKey? _current;
    private CompletionRequestKey? _site;
    private long _generation;
    private long _revision = -1;
    private long _assemblyVersion = -1;
    private readonly CancellationTokenSource _watchCancellation = new();
    private Task? _watch;
    private Task? _settlement;
    private volatile bool _watchFailed;
    private bool _stopped;

    /// <summary>
    /// Initializes one app's requester for its engine and shared syntax vocabulary.
    /// </summary>
    /// <param name="engine">The engine serving this app.</param>
    public CompletionRequester(IReplEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _classifier = new CaretClassifier(new CilTokenizer(engine.Vocabulary));
    }

    /// <summary>
    /// Whether the current query or its next page is still pending.
    /// </summary>
    public bool IsPending => _pending is not null;

    /// <summary>
    /// The engine's current semantic revision, also captured when input is returned to the editor.
    /// </summary>
    public long Revision => _engine.Status.Revision;

    /// <summary>
    /// The current pending query, including a next-page cursor when applicable.
    /// </summary>
    public CompletionRequestKey? PendingKey => _pending?.Key;

    /// <summary>
    /// The last successfully answered local query, excluding failed and cancelled requests.
    /// </summary>
    public CompletionRequestKey? LastAnswered { get; private set; }

    /// <summary>
    /// Reports whether a reply belongs to this requester's current lifetime generation.
    /// </summary>
    /// <param name="generation">The generation captured when its task started.</param>
    /// <returns>Whether that task can still publish.</returns>
    public bool IsCurrent(long generation) => !_stopped && Interlocked.Read(ref _generation) == generation;

    /// <summary>
    /// Checks the document and session directly before retained rows can be displayed or accepted.
    /// </summary>
    /// <param name="state">The prompt.</param>
    /// <param name="snapshot">The retained rows.</param>
    /// <returns>Whether every local query component is still current.</returns>
    public bool Matches(PromptState state, CompletionSnapshot snapshot) => !_stopped && !_watchFailed && !state.Busy
        && snapshot.Key.SameQuery(CurrentKey(state)) && snapshot.Reply.Revision == _engine.Status.Revision
        && snapshot.Reply.AssemblyVersion == _engine.AssemblyVersion;

    /// <summary>
    /// Starts only the work needed by this frame, preserving an identical pending query or page.
    /// </summary>
    /// <param name="state">The prompt on the render thread.</param>
    public void Refresh(PromptState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Prune();
        if (_stopped)
        {
            return;
        }

        _watch ??= WatchAssembliesAsync(state);
        if (_watchFailed)
        {
            Cancel(state);
            state.Palette = PaletteMode.Faulted;
            return;
        }

        if (state.Busy)
        {
            Cancel(state);
            return;
        }

        if (_revision != _engine.Status.Revision || _assemblyVersion != _engine.AssemblyVersion)
        {
            Cancel(state);
            state.Anchors.Clear();
            _revision = _engine.Status.Revision;
            _assemblyVersion = _engine.AssemblyVersion;
            if (state.Palette is not (PaletteMode.Dismissed or PaletteMode.Faulted) || state.DismissedRevision != _revision)
            {
                state.Palette = PaletteMode.Closed;
            }
        }

        var key = CurrentKey(state);
        if (_site is { } previous && (previous.Version != key.Version || previous.Document.Line != key.Document.Line
            || previous.Document.Caret != key.Document.Caret || previous.Selection != key.Selection))
        {
            state.SelectedIndex = 0;
            state.PaletteNavigated = false;
            state.DetailScroll = 0;
            state.Prediction.Hide();
        }

        _site = key;
        state.Site = key.Site;
        if (state.PendingDisplay is { } display && (display.Key.Revision != key.Revision
            || display.Key.Document.Line != key.Document.Line || display.Key.Site.Kind != key.Site.Kind
            || display.Key.Site.Owner != key.Site.Owner || key.Selection is not null))
        {
            state.PendingDisplay = null;
        }
        if (!key.Site.IsOperand)
        {
            Cancel(state);
            return;
        }

        if (state.Palette is PaletteMode.Dismissed or PaletteMode.Faulted && !state.ExplicitCompletion)
        {
            if (state.DismissedVersion == key.Version && state.DismissedCaret == (key.Document.Line, key.Document.Caret))
            {
                return;
            }

            state.Palette = PaletteMode.Closed;
        }

        if (PendingKey?.SameQuery(key) == true)
        {
            if (state.Completions is null)
            {
                state.Palette = PaletteMode.Requested;
            }

            return;
        }

        if (state.MoreCompletions && state.Completions is { Reply.Cursor: not null } current && Matches(state, current))
        {
            state.MoreCompletions = false;
            Start(state, key with { Cursor = current.Reply.Cursor });
            return;
        }

        if (LastAnswered?.SameQuery(key) == true)
        {
            return;
        }

        if (!state.ExplicitCompletion && key.Site.Prefix.Length == 0
            && key.Site.Kind is CompletionSiteKind.Type or CompletionSiteKind.MemberHead)
        {
            Cancel(state);
            return;
        }

        CancelPending();
        state.Completions = null;
        state.Palette = PaletteMode.Requested;
        Start(state, key);
    }

    /// <summary>
    /// Requests an explicit retry or opens a broad operand site after Tab.
    /// </summary>
    /// <param name="state">The prompt.</param>
    public void Request(PromptState state)
    {
        state.ExplicitCompletion = true;
        state.Palette = PaletteMode.Closed;
        LastAnswered = null;
        Refresh(state);
        state.Invalidate?.Invoke();
    }

    /// <summary>
    /// Schedules the next page without cancelling an identical page already in flight.
    /// </summary>
    /// <param name="state">The prompt.</param>
    public void RequestMore(PromptState state)
    {
        if (!IsPending && state.Completions is { Reply.Cursor: not null } snapshot && Matches(state, snapshot))
        {
            state.MoreCompletions = true;
            state.Invalidate?.Invoke();
        }
    }

    /// <summary>
    /// Applies a completed request only when its generation and full query still match the prompt.
    /// </summary>
    /// <param name="state">The prompt on the render thread.</param>
    /// <param name="result">The settled request.</param>
    public void Apply(PromptState state, CompletionResult result)
    {
        if (!IsCurrent(result.Generation))
        {
            return;
        }

        _pending = null;
        if (!result.Key.SameQuery(CurrentKey(state)) || state.Busy)
        {
            LastAnswered = null;
            return;
        }

        state.PendingDisplay = null;
        if (result.Cancelled || result.Faulted)
        {
            LastAnswered = null;
            state.Completions = null;
            state.Palette = result.Faulted ? PaletteMode.Faulted : PaletteMode.Closed;
            state.DismissedVersion = state.Editor.Document.Version;
            state.DismissedRevision = Revision;
            state.DismissedCaret = (state.CaretLine - 1, state.CaretColumn);
            state.ExplicitCompletion = false;
            return;
        }

        var reply = result.Reply!;
        var snapshot = reply.Total < 0 || reply.Revision != result.Key.Revision
            || reply.AssemblyVersion != _engine.AssemblyVersion ? null
            : result.Key.Cursor is null ? new CompletionSnapshot(result.Key, reply) : state.Completions?.Append(reply);
        if (snapshot is null)
        {
            Cancel(state);
            state.Palette = PaletteMode.Closed;
            state.Invalidate?.Invoke();
            return;
        }

        LastAnswered = result.Key with { Cursor = null };
        state.Completions = snapshot;
        state.Palette = reply.Items.Count > 0 || reply.Cursor is not null || snapshot.Reply.Items.Count > 0
            ? PaletteMode.Open : PaletteMode.Closed;
        if (snapshot.Reply.Items.Count == 0 && reply.Cursor is not null)
        {
            state.MoreCompletions = true;
        }
    }

    /// <summary>
    /// Cancels current work and removes every committable row while retaining tasks until they settle.
    /// </summary>
    /// <param name="state">The prompt.</param>
    public void Cancel(PromptState state)
    {
        CancelPending();
        LastAnswered = null;
        state.Completions = null;
        state.PendingDisplay = null;
        state.MoreCompletions = false;
        state.Prediction.Hide();
        if (state.Palette is PaletteMode.Requested or PaletteMode.Open)
        {
            state.Palette = PaletteMode.Closed;
        }
    }

    /// <summary>
    /// Cancels every owned task and waits for settlement, closing a failed engine if the first wait times out.
    /// </summary>
    /// <param name="timeout">The maximum wait before closing a failed engine.</param>
    /// <returns>A task that completes only after every owned request has settled.</returns>
    public Task SettleAsync(TimeSpan timeout) => _settlement ??= SettleCoreAsync(timeout);

    private async Task SettleCoreAsync(TimeSpan timeout)
    {
        _stopped = true;
        Interlocked.Increment(ref _generation);
        foreach (var pending in _owned)
        {
            await pending.Cancellation.CancelAsync().ConfigureAwait(false);
        }

        await _watchCancellation.CancelAsync().ConfigureAwait(false);
        var joined = Task.WhenAll(_owned.Select(pending => pending.Task).Append(_watch ?? Task.CompletedTask));
        try
        {
            await joined.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _engine.DisposeAsync().AsTask().WaitAsync(timeout).ConfigureAwait(false);
            await joined.WaitAsync(timeout).ConfigureAwait(false);
        }

        _pending = null;
        Prune();
        _current = null;
        _site = null;
        LastAnswered = null;
        _watchCancellation.Dispose();
    }

    private CompletionRequestKey CurrentKey(PromptState state)
    {
        var document = state.Editor.Document;
        var line = state.CaretLine - 1;
        var caret = state.CaretColumn;
        var selection = state.Editor.Cursor.HasSelection ? state.Editor.Cursor.SelectionRange : (Hex1b.Documents.DocumentRange?)null;
        if (_current is { } known && known.Version == document.Version && known.Revision == _engine.Status.Revision
            && known.AssemblyVersion == _engine.AssemblyVersion
            && known.Document.Line == line && known.Document.Caret == caret && known.Selection == selection
            && known.Document.Explicit == state.ExplicitCompletion && known.AnchorVersion == state.Anchors.Version)
        {
            return known;
        }

        state.Highlighter.CommentOpenAtStart = _engine.Status.Mark.InBlockComment;
        var comment = state.CommentOpenBefore(line + 1);
        var site = _classifier.Classify(state.CurrentLine, caret, comment);
        if (!site.IsOperand && comment && line > 0)
        {
            // An earlier refused line can roll back its opening comment. Only the engine can bind
            // that prefix, so let it decide this uncertain site; a real comment receives no rows.
            site = _classifier.Classify(state.CurrentLine, caret, false);
        }
        var request = new CompletionRequest(state.Text.Split('\n'), line, caret, null, state.Anchors.Snapshot(), state.ExplicitCompletion);
        _current = new CompletionRequestKey(new CompletionDocumentKey(request), document.Version, _engine.Status.Revision,
            site, selection, state.Anchors.Version) { AssemblyVersion = _engine.AssemblyVersion };
        return _current;
    }

    private async Task WatchAssembliesAsync(PromptState state)
    {
        var version = _engine.AssemblyVersion;
        try
        {
            while (true)
            {
                version = await _engine.WaitForAssembliesAsync(version, _watchCancellation.Token).ConfigureAwait(false);
                state.Invalidate?.Invoke();
            }
        }
        catch (OperationCanceledException) when (_watchCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            _watchFailed = true;
            state.Invalidate?.Invoke();
        }
    }

    private void Start(PromptState state, CompletionRequestKey key)
    {
        var cancellation = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref _generation);
        var task = ExecuteAsync(key, generation, cancellation.Token);
        var completed = task.IsCompleted;
        var relay = completed ? Task.CompletedTask : RelayAsync(task, state);
        var pending = new PendingCompletion(key, cancellation, relay);
        _owned.Add(pending);
        _pending = pending;
        if (completed)
        {
            Apply(state, task.GetAwaiter().GetResult());
        }
    }

    private async Task<CompletionResult> ExecuteAsync(CompletionRequestKey key, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _engine.CompleteAsync(key.Document.Request(key.Cursor), cancellationToken).ConfigureAwait(false);
            return new CompletionResult(generation, key, reply);
        }
        catch (OperationCanceledException)
        {
            return new CompletionResult(generation, key, null, Cancelled: true);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return new CompletionResult(generation, key, null, Faulted: true);
        }
    }

    private async Task RelayAsync(Task<CompletionResult> task, PromptState state)
    {
        var result = await task.ConfigureAwait(false);
        if (IsCurrent(result.Generation))
        {
            state.Post(SubmissionEvent.Completion(result));
        }
    }

    private void CancelPending()
    {
        Interlocked.Increment(ref _generation);
        _pending?.Cancellation.Cancel();
        _pending = null;
    }

    private void Prune()
    {
        for (var index = _owned.Count - 1; index >= 0; index--)
        {
            if (_owned[index].Task.IsCompleted && !ReferenceEquals(_owned[index], _pending))
            {
                _owned[index].Cancellation.Dispose();
                _owned.RemoveAt(index);
            }
        }
    }
}
