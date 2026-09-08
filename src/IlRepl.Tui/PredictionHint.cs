using Hex1b.Documents;

namespace IlRepl.Tui;

/// <summary>
/// The suffix the best-matching completion would add to the word being typed, shown as grey
/// text from the caret on. The view draws it, so the caret stays on what was typed and the
/// suggestion follows it; Right accepts it.
/// </summary>
public sealed class PredictionHint
{
    /// <summary>
    /// The suffix showing, or null.
    /// </summary>
    public string? Suffix { get; private set; }

    /// <summary>
    /// Where the suffix starts: the caret, at the end of its line.
    /// </summary>
    public DocumentPosition At { get; private set; }

    /// <summary>
    /// Shows a suffix at a position.
    /// </summary>
    /// <param name="at">The position, the caret's.</param>
    /// <param name="suffix">The text the best match would add.</param>
    public void Show(DocumentPosition at, string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        Suffix = suffix;
        At = at;
    }

    /// <summary>
    /// Takes the suffix away.
    /// </summary>
    public void Hide() => Suffix = null;
}
