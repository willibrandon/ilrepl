using System.Globalization;
using System.Text;
using Hex1b;

namespace IlRepl.Tui;

/// <summary>
/// Clips and wraps completion text by terminal cells while preserving complete Unicode text elements.
/// </summary>
public static class PaletteText
{
    /// <summary>
    /// Clips a string to a cell width and optionally marks omitted text with an ellipsis.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="width">The available terminal cells.</param>
    /// <param name="ellipsis">Whether to mark truncation.</param>
    /// <returns>The text that fits.</returns>
    public static string Clip(string text, int width, bool ellipsis = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (width <= 0)
        {
            return "";
        }

        if (DisplayWidth.GetStringWidth(text) <= width)
        {
            return text;
        }

        var available = width - (ellipsis ? 1 : 0);
        var used = 0;
        var index = 0;
        while (index < text.Length)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(index));
            var cells = DisplayWidth.GetStringWidth(text.Substring(index, length));
            if (used + cells > available)
            {
                break;
            }

            used += cells;
            index += length;
        }

        return text[..index] + (ellipsis ? "…" : "");
    }

    /// <summary>
    /// Clips and pads a column to exactly the requested number of terminal cells.
    /// </summary>
    /// <param name="text">The column text.</param>
    /// <param name="width">The column width.</param>
    /// <returns>The padded column.</returns>
    public static string Column(string text, int width)
    {
        var clipped = Clip(text, Math.Max(0, width - 1));
        return clipped + new string(' ', Math.Max(0, width - DisplayWidth.GetStringWidth(clipped)));
    }

    /// <summary>
    /// Wraps every character of a complete signature into terminal-cell-sized lines.
    /// </summary>
    /// <param name="text">The complete detail, including explicit line breaks.</param>
    /// <param name="width">The detail pane's width.</param>
    /// <returns>Every wrapped line, without truncating long signatures.</returns>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<string>();
        var line = new StringBuilder();
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var cells = DisplayWidth.GetStringWidth(element);
            if (element is "\n" or "\r\n" || used + cells > Math.Max(1, width) && line.Length > 0)
            {
                result.Add(line.ToString());
                line.Clear();
                used = 0;
            }

            if (element is not ("\n" or "\r\n"))
            {
                line.Append(element);
                used += cells;
            }
        }

        result.Add(line.ToString());
        return result;
    }
}
