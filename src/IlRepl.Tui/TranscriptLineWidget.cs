using Hex1b;
using Hex1b.Composition;
using Hex1b.Theming;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Renders one transcript line as one or more rows of styled runs, folded to the width the
/// terminal reported, so long output is read in full instead of being cut off at the edge.
/// </summary>
/// <param name="Line">The line to render.</param>
/// <param name="Width">The width in columns to fold at, or zero or less to leave the line whole.</param>
/// <param name="Flash">True to draw the line highlighted, right after it was copied.</param>
public sealed record TranscriptLineWidget(TranscriptLine Line, int Width, bool Flash = false) : Hex1bWidget
{
    private static readonly Hex1bColor s_flashBackground = Hex1bColor.FromRgb(46, 92, 60);

    /// <summary>
    /// Builds the rows.
    /// </summary>
    /// <param name="ctx">The composition context.</param>
    /// <returns>The widget tree for the line.</returns>
    protected override Hex1bWidget Build(CompositionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var rows = TranscriptLineFolder.Fold(Line.Spans, Width);
        var content = rows.Count == 1
            ? Row(ctx, rows[0])
            : ctx.VStack(v => rows.Select(row => Row(v, row)).ToArray());
        return Flash
            ? ctx.ThemePanel(theme => theme.Clone().Set(GlobalTheme.BackgroundColor, s_flashBackground), content)
            : content;
    }

    private static Hex1bWidget Row<TParent>(WidgetContext<TParent> ctx, IReadOnlyList<TranscriptSpan> spans)
        where TParent : Hex1bWidget
    {
        if (spans.Count == 0)
        {
            return ctx.Text("");
        }

        if (spans.Count == 1 && spans[0].Style == SpanStyle.Default)
        {
            return ctx.Text(spans[0].Text);
        }

        return ctx.HStack(h => spans
            .Select(s => s.Style == SpanStyle.Default
                ? (Hex1bWidget)h.Text(s.Text).ContentWidth()
                : h.ThemePanel(SpanPalette.Mutator(s.Style), h.Text(s.Text).ContentWidth()).ContentWidth())
            .ToArray());
    }
}
