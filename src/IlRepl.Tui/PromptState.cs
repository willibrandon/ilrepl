using Hex1b.Widgets;

namespace IlRepl.Tui;

/// <summary>
/// The hoisted state of the prompt: the text box, the completion palette selection, and the
/// input history.
/// </summary>
public sealed class PromptState
{
    /// <summary>
    /// The text box state the prompt is bound to.
    /// </summary>
    public TextBoxState TextBox { get; } = new();

    /// <summary>
    /// The highlighted row of the completion palette.
    /// </summary>
    public int SelectedIndex { get; set; }

    /// <summary>
    /// True once the user dismissed the palette with Escape; typing shows it again.
    /// </summary>
    public bool PaletteDismissed { get; set; }

    /// <summary>
    /// Previously submitted lines, oldest first.
    /// </summary>
    public List<string> History { get; } = [];

    /// <summary>
    /// The history row being viewed, or <see cref="History"/>.Count when editing a new line.
    /// </summary>
    public int HistoryIndex { get; set; }

    /// <summary>
    /// The unfinished line saved while walking history.
    /// </summary>
    public string? Stash { get; set; }
}
