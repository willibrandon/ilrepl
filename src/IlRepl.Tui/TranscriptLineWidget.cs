using Hex1b;
using Hex1b.Composition;
using Hex1b.Nodes;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Renders complete styled transcript text as cached rows folded to the terminal width.
/// </summary>
/// <param name="Line">The line to render.</param>
/// <param name="Width">The width in columns to fold at, or zero or less to leave the line whole.</param>
/// <param name="Flash">True to draw the line highlighted, right after it was copied.</param>
public sealed record TranscriptLineWidget(TranscriptLine Line, int Width, bool Flash = false) : Hex1bWidget
{
    private (TranscriptLine Line, int Width, IReadOnlyList<IReadOnlyList<TranscriptSpan>> Rows)? _folded;

    /// <summary>
    /// Reuses a complete rendered row when its last paint still matches the inherited colors.
    /// </summary>
    internal TranscriptLineWidget CacheRendering() => this.Cached(static context =>
        context.Node.GetChildren().FirstOrDefault() is ThemePanelNode { ThemeMutator.Target: TranscriptRenderState state }
        && state.CanReuse(context));

    /// <summary>
    /// The folded rows, retained across reconciliation and rebuilt when a record copy changes its line or width.
    /// </summary>
    internal IReadOnlyList<IReadOnlyList<TranscriptSpan>> Rows
    {
        get
        {
            if (_folded is not { } folded || !ReferenceEquals(folded.Line, Line) || folded.Width != Width)
            {
                _folded = (Line, Width, TranscriptLineFolder.Fold(Line.Spans, Width));
            }

            return _folded.Value.Rows;
        }
    }

    /// <summary>
    /// Reuses the styled widget tree until its source, wrapping width, or copy feedback changes.
    /// </summary>
    /// <param name="ctx">The composition context.</param>
    /// <returns>The widget tree for the line.</returns>
    protected override Hex1bWidget Build(CompositionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.UseState(static () => new TranscriptRenderState()).Build(ctx, this);
    }
}
