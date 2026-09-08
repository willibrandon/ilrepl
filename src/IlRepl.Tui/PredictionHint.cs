using Hex1b.Documents;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// The ghost text after the caret: the rest of the best completion, dimmed, drawn as an inline
/// hint through the editor session the provider is activated with.
/// </summary>
public sealed class PredictionHint : ITextDecorationProvider
{
    private IEditorSession? _session;
    private DocumentPosition _at;

    /// <summary>
    /// The suffix showing, or null.
    /// </summary>
    public string? Suffix { get; private set; }

    /// <inheritdoc />
    public void Activate(IEditorSession session) => _session = session;

    /// <inheritdoc />
    public void Deactivate() => _session = null;

    /// <inheritdoc />
    public IReadOnlyList<TextDecorationSpan> GetDecorations(int startLine, int endLine, IHex1bDocument document) => [];

    /// <summary>
    /// Shows a suffix at a position, or leaves it if it is already there.
    /// </summary>
    /// <param name="at">Where the suffix goes.</param>
    /// <param name="suffix">The suffix.</param>
    public void Show(DocumentPosition at, string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        if (_session is null || (Suffix == suffix && _at == at))
        {
            return;
        }

        Suffix = suffix;
        _at = at;
        _session.PushInlineHints([new InlineHint(at, suffix, new TextDecoration { Foreground = SpanPalette.Color(SpanStyle.Dim) })]);
    }

    /// <summary>
    /// Takes the suffix away.
    /// </summary>
    public void Hide()
    {
        if (Suffix is null)
        {
            return;
        }

        Suffix = null;
        _session?.ClearInlineHints();
    }
}
