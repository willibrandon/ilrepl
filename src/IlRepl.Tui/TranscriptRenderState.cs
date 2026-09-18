using Hex1b;
using Hex1b.Composition;
using Hex1b.Theming;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Retains one transcript node's styled tree and tracks actual paints independently of cache eligibility checks.
/// </summary>
internal sealed class TranscriptRenderState
{
    private static readonly Hex1bColor s_flashBackground = Hex1bColor.FromRgb(46, 92, 60);
    private (TranscriptLine Line, int Width, bool Flash, Hex1bWidget Widget)? _content;
    private (Hex1bColor Foreground, Hex1bColor Background, Hex1bColor Ambient, long Paint)? _cached;
    private long _paint;

    /// <summary>
    /// Reuses a surface only when its inherited colors still match and no intervening dirty render bypassed the predicate.
    /// </summary>
    internal bool CanReuse(RenderCacheContext context)
    {
        var colors = (context.RenderContext.Theme.GetGlobalForeground(), context.RenderContext.Theme.GetGlobalBackground(),
            context.RenderContext.AmbientBackground, _paint);
        if (_cached is { } cached && cached.Equals(colors)) return true;

        // A rejected hit paints the uncached observer and its whole styled subtree exactly once.
        _cached = (colors.Item1, colors.Item2, colors.Item3, _paint + 1);
        return false;
    }

    /// <summary>
    /// Retains the immutable styled tree for this node until its content, width, or copy feedback changes.
    /// </summary>
    internal Hex1bWidget Build(CompositionContext context, TranscriptLineWidget source)
    {
        if (_content is { } existing && ReferenceEquals(existing.Line, source.Line)
            && existing.Width == source.Width && existing.Flash == source.Flash) return existing.Widget;

        var rows = source.Rows;
        var content = rows.Count == 1
            ? Row(context, rows[0])
            : context.VStack(v => rows.Select(row => Row(v, row)).ToArray()).Cached(static _ => false);
        var widget = context.ThemePanel(Paint, content).Cached(static _ => false);
        _content = (source.Line, source.Width, source.Flash, widget);
        return widget;
    }

    private Hex1bTheme Paint(Hex1bTheme theme)
    {
        _paint++;
        return _content is { Flash: true } ? theme.Set(GlobalTheme.BackgroundColor, s_flashBackground) : theme;
    }

    private static Hex1bWidget Row<TParent>(WidgetContext<TParent> context, IReadOnlyList<TranscriptSpan> spans)
        where TParent : Hex1bWidget
    {
        if (spans.Count == 0) return context.Text("").Cached(static _ => false);
        if (spans.Count == 1 && spans[0].Style == SpanStyle.Default)
        {
            return context.Text(spans[0].Text).Cached(static _ => false);
        }

        return context.HStack(h => spans.Select(span => span.Style == SpanStyle.Default
            ? (Hex1bWidget)h.Text(span.Text).ContentWidth().Cached(static _ => false)
            : h.ThemePanel(SpanPalette.Mutator(span.Style), h.Text(span.Text).ContentWidth().Cached(static _ => false))
                .ContentWidth().Cached(static _ => false)).ToArray()).Cached(static _ => false);
    }
}
