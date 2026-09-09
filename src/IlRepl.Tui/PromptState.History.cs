namespace IlRepl.Tui;

public sealed partial class PromptState
{
    private int _pendingHistoryBacks;
    private long _historyRequestVersion;
    private int _historyRequestCaret;

    /// <summary>
    /// Whether the initial history snapshot is still being read.
    /// </summary>
    public bool HistoryLoading { get; set; }

    /// <summary>
    /// Recalls an entry or retains navigation that must wait for the initial history snapshot.
    /// </summary>
    /// <param name="back">Whether to move toward older entries.</param>
    public void NavigateHistory(bool back)
    {
        if (!HistoryRequestIsCurrent)
        {
            _pendingHistoryBacks = 0;
        }

        if (!back && _pendingHistoryBacks > 0)
        {
            _pendingHistoryBacks--;
            return;
        }

        var text = back ? History.Back(Text) : History.Forward(Text);
        if (text is not null)
        {
            RecallHistory(text);
        }
        else if (back && HistoryLoading)
        {
            _pendingHistoryBacks = Math.Min(_pendingHistoryBacks + 1, PromptHistory.MaxEntries);
            _historyRequestVersion = Editor.Document.Version;
            _historyRequestCaret = Editor.Cursor.Position.Value;
        }
    }

    /// <summary>
    /// Merges stored history and fulfills waiting navigation if the user has left the draft untouched.
    /// </summary>
    /// <param name="snapshot">The initial stored entries.</param>
    public void LoadHistory(HistorySnapshot snapshot)
    {
        History.Load(snapshot);
        HistoryLoading = false;
        var pending = HistoryRequestIsCurrent ? _pendingHistoryBacks : 0;
        _pendingHistoryBacks = 0;
        var current = Text;
        var recalled = false;
        for (var index = 0; index < pending; index++)
        {
            if (History.Back(current) is not { } text)
            {
                break;
            }

            current = text;
            recalled = true;
        }

        if (recalled)
        {
            RecallHistory(current);
        }
    }

    private bool HistoryRequestIsCurrent => _historyRequestVersion == Editor.Document.Version
        && _historyRequestCaret == Editor.Cursor.Position.Value && !Editor.Cursor.HasSelection;

    private void RecallHistory(string text)
    {
        SetText(text, text.Length);
        PaletteDismissed = true;
        PaletteNavigated = false;
        Prediction.Hide();
    }
}
