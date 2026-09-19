namespace IlRepl.Protocol;

/// <summary>
/// Starts a runtime independently of construction so the terminal can accept editing immediately.
/// </summary>
public sealed partial class SessionController
{
    private readonly CancellationTokenSource? _startupCancellation;

    /// <summary>
    /// Creates an editable controller before launching the initial execution host.
    /// </summary>
    /// <param name="start">The factory shared by initial startup and explicit runtime replacement.</param>
    /// <param name="initialRequest">An optional session document to open after the first host connects.</param>
    /// <param name="historyLineLimit">The history row limit established before startup, or zero for unlimited output.</param>
    public SessionController(
        Func<CancellationToken, Task<IReplEngine>> start,
        SessionRequest? initialRequest = null,
        int historyLineLimit = 0) : this(new InactiveEngine(), start, historyLineLimit)
    {
        _runtimeState = SessionRuntimeState.Starting;
        _startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Initialization = Task.Run(() => InitializeAsync(initialRequest, _startupCancellation.Token), CancellationToken.None);
    }

    /// <summary>
    /// Settles after initial startup succeeds, fails, or is cancelled while leaving the editor available.
    /// </summary>
    public Task Initialization { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Cancels initial connection and launch without discarding the current editor text.
    /// </summary>
    public void CancelStartup() => _startupCancellation?.Cancel();

    private async Task InitializeAsync(SessionRequest? initialRequest, CancellationToken cancellationToken)
    {
        IReplEngine? candidate = null;
        try
        {
            candidate = await _start(cancellationToken).ConfigureAwait(false);
            var opened = initialRequest is null ? null
                : await candidate.SessionAsync(initialRequest with
                {
                    HistoryLineLimit = initialRequest.HistoryLineLimit ?? HistoryLineLimit,
                }, cancellationToken).ConfigureAwait(false);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (RuntimeState != SessionRuntimeState.Starting)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                var previous = _engine;
                await InstallEngineAsync(candidate).ConfigureAwait(false);
                await previous.DisposeAsync().ConfigureAwait(false);
                if (opened is not null)
                {
                    var saved = opened.Document.Editor;
                    var editor = saved.WithStartupInput(Editor);
                    Editor = editor;
                    Workspace = opened with
                    {
                        Document = opened.Document with { Editor = editor },
                        Dirty = opened.Dirty || !SameEditorText(saved, editor),
                    };

                    RecoveryCompleted?.Invoke(Workspace with { StartupEditor = saved });
                }

                SetRuntimeState(SessionRuntimeState.Ready);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception exception)
        {
            if (candidate is not null)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }

            if (_disposed || RuntimeState != SessionRuntimeState.Starting)
            {
                return;
            }

            SetRuntimeState(SessionRuntimeState.Unavailable);
            Workspace = new SessionReply { Document = new SessionDocument { Editor = Editor },
                Reply = Failure(exception is OperationCanceledException ? "host startup cancelled; use .session restart to try again"
                    : "host unavailable: " + exception.Message + "; use .session restart to try again") };
            RecoveryCompleted?.Invoke(Workspace);
        }
    }
}
