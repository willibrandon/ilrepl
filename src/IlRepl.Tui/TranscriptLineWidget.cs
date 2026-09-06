using Hex1b;
using Hex1b.Composition;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Renders one transcript line: plain text for a single default span, otherwise a row of
/// colored text runs.
/// </summary>
/// <param name="Line">The line to render.</param>
public sealed record TranscriptLineWidget(TranscriptLine Line) : Hex1bWidget
{
    /// <summary>
    /// Builds the row.
    /// </summary>
    /// <param name="ctx">The composition context.</param>
    /// <returns>The widget tree for the line.</returns>
    protected override Hex1bWidget Build(CompositionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var spans = Line.Spans;
        if (spans.Count == 0)
        {
            return ctx.Text("");
        }

        if (spans.Count == 1 && spans[0].Style == SpanStyle.Default)
        {
            return ctx.Text(spans[0].Text);
        }

        return ctx.HStack(h => spans
            .Where(s => s.Text.Length > 0)
            .Select(s => s.Style == SpanStyle.Default
                ? (Hex1bWidget)h.Text(s.Text).ContentWidth()
                : h.ThemePanel(SpanPalette.Mutator(s.Style), h.Text(s.Text).ContentWidth()).ContentWidth())
            .ToArray());
    }
}
