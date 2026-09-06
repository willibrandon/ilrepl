using System.Globalization;
using Hex1b;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Folds a transcript line into rows that fit a width, keeping each run's style. Rows break at
/// the last space that fits, or inside a word when nothing else fits. Leading spaces are kept,
/// so listings keep their indentation. A line made of several runs gets a hanging indent: the
/// rows after the first start under the last run, so a description folds beneath itself and not
/// beneath its label.
/// </summary>
public static class TranscriptLineFolder
{
    /// <summary>
    /// The fewest columns a hanging indent may leave for the folded text.
    /// </summary>
    public const int MinimumFoldWidth = 20;

    /// <summary>
    /// Folds the spans of one line into rows no wider than <paramref name="width"/>.
    /// </summary>
    /// <param name="spans">The runs of the line.</param>
    /// <param name="width">The width in columns, or zero or less for no folding.</param>
    /// <returns>One or more rows, each a list of runs.</returns>
    public static IReadOnlyList<IReadOnlyList<TranscriptSpan>> Fold(IReadOnlyList<TranscriptSpan> spans, int width)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var runs = spans.Where(s => s.Text.Length > 0).ToList();
        var cells = new List<Cell>();
        foreach (var span in runs)
        {
            var elements = StringInfo.GetTextElementEnumerator(span.Text);
            while (elements.MoveNext())
            {
                var grapheme = (string)elements.Current;
                cells.Add(new Cell(grapheme, DisplayWidth.GetGraphemeWidth(grapheme), span.Style));
            }
        }

        var total = cells.Sum(c => c.Width);
        if (width <= 0 || total <= width)
        {
            return [runs];
        }

        var indent = HangingIndent(runs, width);
        var rows = new List<IReadOnlyList<TranscriptSpan>>();
        var start = 0;
        while (start < cells.Count)
        {
            var first = rows.Count == 0;
            var limit = first ? width : width - indent;

            // Take cells until the next one would not fit. A cell wider than the limit is taken alone.
            var end = start;
            var used = 0;
            while (end < cells.Count && used + cells[end].Width <= limit)
            {
                used += cells[end].Width;
                end++;
            }

            if (end == start)
            {
                end = start + 1;
            }

            if (end >= cells.Count)
            {
                rows.Add(Row(cells, start, cells.Count, first ? 0 : indent));
                break;
            }

            // Break at the last space that fits, dropping that space. Otherwise break the word.
            var breakAt = -1;
            for (var i = end - 1; i > start; i--)
            {
                if (cells[i].Text == " ")
                {
                    breakAt = i;
                    break;
                }
            }

            if (breakAt > start)
            {
                rows.Add(Row(cells, start, breakAt, first ? 0 : indent));
                start = breakAt + 1;
            }
            else
            {
                rows.Add(Row(cells, start, end, first ? 0 : indent));
                start = end;
            }
        }

        return rows;
    }

    private static int HangingIndent(List<TranscriptSpan> runs, int width)
    {
        if (runs.Count < 2)
        {
            return 0;
        }

        // The indent is dropped when it would leave too little room for the text that follows.
        var indent = runs.Take(runs.Count - 1).Sum(r => DisplayWidth.GetStringWidth(r.Text));
        return width - indent >= MinimumFoldWidth ? indent : 0;
    }

    private static List<TranscriptSpan> Row(List<Cell> cells, int start, int end, int indent)
    {
        var row = new List<TranscriptSpan>();
        if (indent > 0)
        {
            row.Add(new TranscriptSpan(new string(' ', indent)));
        }

        var i = start;
        while (i < end)
        {
            var style = cells[i].Style;
            var j = i;
            var text = new System.Text.StringBuilder();
            while (j < end && cells[j].Style == style)
            {
                text.Append(cells[j].Text);
                j++;
            }

            row.Add(new TranscriptSpan(text.ToString(), style));
            i = j;
        }

        return row;
    }

    private readonly record struct Cell(string Text, int Width, SpanStyle Style);
}
