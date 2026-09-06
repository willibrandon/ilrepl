using Hex1b.Theming;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Maps transcript span styles to colors for the terminal UI.
/// </summary>
public static class SpanPalette
{
    /// <summary>
    /// The color for a style, or <see cref="Hex1bColor.Default"/> for plain text.
    /// </summary>
    /// <param name="style">The style.</param>
    /// <returns>The color.</returns>
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
        _ => Hex1bColor.Default,
    };

    /// <summary>
    /// A theme mutator that sets the foreground color for a style.
    /// </summary>
    /// <param name="style">The style.</param>
    /// <returns>The mutator to pass to a theme panel.</returns>
    public static Func<Hex1bTheme, Hex1bTheme> Mutator(SpanStyle style)
    {
        var color = Color(style);
        return theme => theme.Clone().Set(GlobalTheme.ForegroundColor, color);
    }
}
