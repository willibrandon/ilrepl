namespace IlRepl.Tui;

/// <summary>
/// Keeps the caret in view: the offsets move only as far as they must to bring the caret cell
/// inside the viewport, and are clamped to the document, so the first frame after any change
/// shows the caret and nothing moves while it stays visible.
/// </summary>
public static class PromptViewport
{
    /// <summary>
    /// Moves the offsets, if they need moving, so the caret is inside the viewport.
    /// </summary>
    /// <param name="current">The offsets as they are.</param>
    /// <param name="rows">The viewport's rows.</param>
    /// <param name="columns">The viewport's columns, the gutter not counted.</param>
    /// <param name="caretLine">The caret's line, counted from one.</param>
    /// <param name="caretColumn">The caret's column, counted from zero.</param>
    /// <param name="lineCount">How many lines the document has.</param>
    /// <returns>The offsets to render with.</returns>
    public static ViewportOffsets Reveal(ViewportOffsets current, int rows, int columns, int caretLine, int caretColumn, int lineCount)
    {
        rows = Math.Max(1, rows);
        columns = Math.Max(1, columns);
        var maxTop = Math.Max(1, lineCount - rows + 1);
        var top = Math.Clamp(current.Top, 1, maxTop);
        if (caretLine < top)
        {
            top = caretLine;
        }
        else if (caretLine >= top + rows)
        {
            top = caretLine - rows + 1;
        }

        var left = Math.Max(0, current.Left);
        if (caretColumn < left)
        {
            left = caretColumn;
        }
        else if (caretColumn >= left + columns)
        {
            left = caretColumn - columns + 1;
        }

        return new ViewportOffsets(Math.Max(1, top), Math.Max(0, left));
    }
}
