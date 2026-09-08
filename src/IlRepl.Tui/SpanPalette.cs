using Hex1b;
using Hex1b.Documents;
using Hex1b.Theming;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// The colours of the terminal UI, one per <see cref="SpanStyle"/>, muted in the way of One Dark.
/// The transcript, the editor, and the status bar all draw from here.
/// </summary>
public static class SpanPalette
{
    /// <summary>
    /// The colour of a style.
    /// </summary>
    /// <param name="style">The style.</param>
    /// <returns>The colour.</returns>
    public static Hex1bColor Color(SpanStyle style) => style switch
    {
        SpanStyle.Dim => Hex1bColor.FromRgb(120, 124, 134),
        SpanStyle.Prompt => Hex1bColor.FromRgb(97, 175, 239),
        SpanStyle.Input => Hex1bColor.FromRgb(220, 223, 228),
        SpanStyle.Opcode => Hex1bColor.FromRgb(86, 182, 194),
        SpanStyle.Type => Hex1bColor.FromRgb(86, 182, 194),
        SpanStyle.TopType => Hex1bColor.FromRgb(152, 255, 238),
        SpanStyle.Label => Hex1bColor.FromRgb(229, 192, 123),
        SpanStyle.Number => Hex1bColor.FromRgb(209, 154, 102),
        SpanStyle.String => Hex1bColor.FromRgb(152, 195, 121),
        SpanStyle.Keyword => Hex1bColor.FromRgb(198, 120, 221),
        SpanStyle.Error => Hex1bColor.FromRgb(224, 108, 117),
        SpanStyle.Command => Hex1bColor.FromRgb(97, 175, 239),
        SpanStyle.Output => Hex1bColor.FromRgb(171, 178, 191),
        SpanStyle.Heading => Hex1bColor.FromRgb(229, 192, 123),
        SpanStyle.Directive => Hex1bColor.FromRgb(171, 178, 191),
        SpanStyle.Comment => Hex1bColor.FromRgb(92, 99, 112),
        SpanStyle.Member => Hex1bColor.FromRgb(97, 175, 239),
        SpanStyle.Punctuation => Hex1bColor.FromRgb(120, 124, 134),
        _ => Hex1bColor.Default,
    };

    /// <summary>
    /// A theme change that paints text in a style's colour.
    /// </summary>
    /// <param name="style">The style.</param>
    /// <returns>The change.</returns>
    public static Func<Hex1bTheme, Hex1bTheme> Mutator(SpanStyle style)
    {
        var color = Color(style);
        return theme => theme.Clone().Set(GlobalTheme.ForegroundColor, color);
    }

    /// <summary>
    /// How a style is drawn inside the editor: its colour, or for an error a curly red underline
    /// under the text's own colour, so a word still being typed is not painted red while the
    /// palette is showing what it could become.
    /// </summary>
    /// <param name="style">The style.</param>
    /// <returns>The decoration, or null for a style drawn as plain text.</returns>
    public static TextDecoration? Decoration(SpanStyle style) => style switch
    {
        SpanStyle.Default or SpanStyle.Input => null,
        SpanStyle.Error => new TextDecoration { UnderlineStyle = UnderlineStyle.Curly, UnderlineColor = Color(SpanStyle.Error) },
        _ => new TextDecoration { Foreground = Color(style) },
    };
}
