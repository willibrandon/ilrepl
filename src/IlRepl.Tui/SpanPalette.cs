using Hex1b;
using Hex1b.Documents;
using Hex1b.Theming;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// The colours of the terminal UI, one per <see cref="SpanStyle"/>, muted in the way of One Dark.
/// The transcript, the editor, and the status bar all draw from here, and so do the docs, which
/// also have the same roles in the colours of One Light for a light ground.
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
    /// The colour of a style on a light ground: the same role in the same hue, darker and more
    /// saturated so it reads on white, in the way of One Light. The top of the stack, brighter
    /// than a type on a dark ground, is a deeper teal than a type here.
    /// </summary>
    /// <param name="style">The style.</param>
    /// <returns>The colour.</returns>
    public static Hex1bColor LightColor(SpanStyle style) => style switch
    {
        SpanStyle.Dim => Hex1bColor.FromRgb(105, 108, 119),
        SpanStyle.Prompt => Hex1bColor.FromRgb(64, 120, 242),
        SpanStyle.Input => Hex1bColor.FromRgb(56, 58, 66),
        SpanStyle.Opcode => Hex1bColor.FromRgb(1, 132, 188),
        SpanStyle.Type => Hex1bColor.FromRgb(1, 132, 188),
        SpanStyle.TopType => Hex1bColor.FromRgb(0, 128, 110),
        SpanStyle.Label => Hex1bColor.FromRgb(193, 132, 1),
        SpanStyle.Number => Hex1bColor.FromRgb(152, 104, 1),
        SpanStyle.String => Hex1bColor.FromRgb(80, 161, 79),
        SpanStyle.Keyword => Hex1bColor.FromRgb(166, 38, 164),
        SpanStyle.Error => Hex1bColor.FromRgb(228, 86, 73),
        SpanStyle.Command => Hex1bColor.FromRgb(64, 120, 242),
        SpanStyle.Output => Hex1bColor.FromRgb(92, 99, 112),
        SpanStyle.Heading => Hex1bColor.FromRgb(193, 132, 1),
        SpanStyle.Directive => Hex1bColor.FromRgb(92, 99, 112),
        SpanStyle.Comment => Hex1bColor.FromRgb(125, 130, 139),
        SpanStyle.Member => Hex1bColor.FromRgb(64, 120, 242),
        SpanStyle.Punctuation => Hex1bColor.FromRgb(105, 108, 119),
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
