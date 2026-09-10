using IlRepl.Protocol;

namespace IlRepl.Tui;

public sealed partial class PromptState
{
    /// <summary>
    /// The live revision at which a palette was explicitly dismissed or refused input was returned.
    /// </summary>
    public long DismissedRevision { get; set; } = -1;

    /// <summary>
    /// The single palette state shared by requests, drawing and acceptance.
    /// </summary>
    public PaletteMode Palette { get; set; }

    /// <summary>
    /// The app's operand requester, or null for a prompt using only the local catalog.
    /// </summary>
    public CompletionRequester? Requester { get; init; }

    /// <summary>
    /// The confirmed operand rows for the current document and session.
    /// </summary>
    public CompletionSnapshot? Completions { get; set; }

    /// <summary>
    /// The previous page, used only for dimmed display while an edited operand awaits its replacement.
    /// </summary>
    public CompletionSnapshot? PendingDisplay { get; set; }

    /// <summary>
    /// The syntax-defined site most recently classified by the requester.
    /// </summary>
    public CompletionSite Site { get; set; } = CompletionSite.None;

    /// <summary>
    /// Selected generic definitions retained while arguments are edited.
    /// </summary>
    public ContinuationAnchors Anchors { get; }

    /// <summary>
    /// Whether the current operand query was explicitly requested.
    /// </summary>
    public bool ExplicitCompletion { get; set; }

    /// <summary>
    /// Whether the next frame should request another page from the current query.
    /// </summary>
    public bool MoreCompletions { get; set; }

    /// <summary>
    /// The document version whose palette was dismissed or faulted.
    /// </summary>
    public long DismissedVersion { get; set; } = -1;

    /// <summary>
    /// The zero-based line and caret whose palette was dismissed or faulted.
    /// </summary>
    public (int Line, int Caret) DismissedCaret { get; set; } = (-1, -1);

    /// <summary>
    /// The selected signature's first visible wrapped detail line.
    /// </summary>
    public int DetailScroll { get; set; }

    private void CompletionTextChanged()
    {
        var display = PendingDisplay ?? Completions;
        Requester?.Cancel(this);
        PendingDisplay = display;
        Completions = null;
        Palette = PaletteMode.Closed;
        ExplicitCompletion = false;
        PaletteNavigated = false;
        SelectedIndex = 0;
        DetailScroll = 0;
        Prediction.Hide();
    }
}
