using System.Globalization;
using Hex1b;
using Hex1b.Documents;
using Hex1b.Layout;
using Hex1b.Theming;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Renders the prompt's buffer: the prompt in a gutter on the first line and a continuation
/// marker on the ones below, then the text, scrolled so the caret is always in view. The offsets
/// are computed from the caret at render time, so the first frame after any change is right and
/// nothing moves while the caret stays visible. Clicks are mapped back through the same offsets.
/// </summary>
public sealed class PromptView : IEditorViewRenderer
{
    private const string Marker = "...> ";
    private readonly TextEditorViewRenderer _inner = TextEditorViewRenderer.Instance;

    /// <summary>
    /// The prompt shown on the first line, such as <c>il[1]&gt; </c>.
    /// </summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// Where the viewport starts.
    /// </summary>
    public ViewportOffsets Offsets { get; private set; } = new(1, 0);

    /// <summary>
    /// The offsets count characters and the screen counts cells: a wide character takes two.
    /// When the cells before the caret overflow the columns, the left offset moves on until
    /// the caret's cell fits.
    /// </summary>
    /// <param name="offsets">The offsets after the character-based reveal.</param>
    /// <param name="columns">The columns the text has.</param>
    /// <param name="line">The caret's line.</param>
    /// <param name="caret">The caret's character index on the line.</param>
    /// <returns>The offsets with the caret's cell in view.</returns>
    public static ViewportOffsets RevealWide(ViewportOffsets offsets, int columns, string line, int caret)
    {
        ArgumentNullException.ThrowIfNull(line);
        var end = Math.Clamp(caret, 0, line.Length);
        var left = ElementStart(line, Math.Clamp(offsets.Left, 0, end));
        while (left < end && DisplayWidth.GetStringWidth(line[left..end]) >= columns)
        {
            left += Math.Max(1, StringInfo.GetNextTextElementLength(line.AsSpan(left)));
        }

        return left == offsets.Left ? offsets : offsets with { Left = Math.Min(left, end) };
    }

    // The character reveal may land inside a surrogate pair or a grapheme; the scroll starts at
    // the text element that holds the index, so the renderer never begins with half a character.
    private static int ElementStart(string line, int index)
    {
        var start = 0;
        while (start < index)
        {
            var length = Math.Max(1, StringInfo.GetNextTextElementLength(line.AsSpan(start)));
            if (start + length > index)
            {
                return start;
            }

            start += length;
        }

        return start;
    }

    /// <summary>
    /// How many columns the gutter takes.
    /// </summary>
    public int GutterWidth => DisplayWidth.GetStringWidth(Label);

    /// <inheritdoc />
    public void Render(Hex1bRenderContext context, EditorState state, Rect viewport, int scrollOffset, int horizontalScrollOffset, bool isFocused, char? pendingNibble = null, IReadOnlyList<ITextDecorationProvider>? decorationProviders = null, IReadOnlyList<InlineHint>? inlineHints = null, bool wordWrap = false, IReadOnlyList<FoldingRegion>? foldingRegions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);
        var gutter = GutterWidth;
        var rows = Math.Max(1, viewport.Height);
        var columns = Math.Max(1, viewport.Width - gutter);
        var document = state.Document;
        var caret = document.OffsetToPosition(new DocumentOffset(Math.Clamp(state.Cursor.Position.Value, 0, document.Length)));
        Offsets = PromptViewport.Reveal(Offsets, rows, columns, caret.Line, caret.Column - 1, document.LineCount);
        Offsets = RevealWide(Offsets, columns, document.GetLineText(caret.Line), caret.Column - 1);

        var prompt = SpanPalette.Color(SpanStyle.Prompt).ToForegroundAnsi();
        var dim = SpanPalette.Color(SpanStyle.Dim).ToForegroundAnsi();
        var reset = Hex1bColor.Default.ToForegroundAnsi();
        for (var row = 0; row < rows; row++)
        {
            var line = Offsets.Top + row;
            var text = line == 1 ? prompt + Label
                : line <= document.LineCount ? dim + Marker.PadLeft(gutter)[^Math.Min(gutter, Marker.Length + gutter)..][..gutter]
                : new string(' ', gutter);
            context.WriteClipped(viewport.X, viewport.Y + row, text + reset);
        }

        _inner.Render(context, state, new Rect(viewport.X + gutter, viewport.Y, columns, rows), Offsets.Top, Offsets.Left, isFocused, pendingNibble, decorationProviders, inlineHints, wordWrap: false, foldingRegions: null);
    }

    /// <inheritdoc />
    public DocumentOffset? HitTest(int localX, int localY, EditorState state, int viewportColumns, int viewportLines, int scrollOffset, int horizontalScrollOffset)
    {
        ArgumentNullException.ThrowIfNull(state);
        var gutter = GutterWidth;
        var document = state.Document;
        if (localX < gutter)
        {
            var line = Offsets.Top + localY;
            return line > document.LineCount ? new DocumentOffset(document.Length) : document.PositionToOffset(new DocumentPosition(Math.Max(1, line), 1));
        }

        return _inner.HitTest(localX - gutter, localY, state, Math.Max(1, viewportColumns - gutter), viewportLines, Offsets.Top, Offsets.Left);
    }

    /// <summary>
    /// One, always: the view scrolls itself, so the editor never shows a scrollbar of its own.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="viewportColumns">The viewport's columns.</param>
    /// <returns>One.</returns>
    public int GetTotalLines(IHex1bDocument document, int viewportColumns) => 1;

    /// <summary>
    /// Zero, always: the view scrolls itself sideways too.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="scrollOffset">The editor's own offset, unused.</param>
    /// <param name="viewportLines">The viewport's rows.</param>
    /// <param name="viewportColumns">The viewport's columns.</param>
    /// <returns>Zero.</returns>
    public int GetMaxLineWidth(IHex1bDocument document, int scrollOffset, int viewportLines, int viewportColumns) => 0;
}
