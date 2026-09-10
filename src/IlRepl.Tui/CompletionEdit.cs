using Hex1b.Documents;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Applies every completion acceptance path as one syntax-defined replacement and one undoable edit.
/// </summary>
/// <param name="Range">The absolute document range replaced.</param>
/// <param name="Text">The complete insertion text.</param>
/// <param name="CaretOffset">The optional caret offset relative to the replacement start.</param>
public readonly record struct CompletionEdit(DocumentRange Range, string Text, int? CaretOffset = null)
{
    /// <summary>
    /// Builds an edit only when the candidate still belongs to the current document and session.
    /// </summary>
    /// <param name="state">The prompt.</param>
    /// <param name="item">The selected row.</param>
    /// <returns>The current edit, or null for stale rows.</returns>
    public static CompletionEdit? For(PromptState state, CompletionItem item)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(item);
        var document = state.Editor.Document;
        var line = state.CurrentLine;
        var lineStart = document.PositionToOffset(new DocumentPosition(state.CaretLine, 1)).Value;
        var start = line.Length - line.TrimStart().Length;
        var length = line.Length - start;
        if (item.Kind != CompletionKind.None)
        {
            if (state.Completions is not { } snapshot || state.Requester?.Matches(state, snapshot) != true
                || state.Palette != PaletteMode.Open || !snapshot.Reply.Items.Contains(item))
            {
                return null;
            }

            start = snapshot.Reply.ReplaceStart;
            length = snapshot.Reply.ReplaceLength;
        }

        if (start < 0 || length < 0 || start > line.Length - length)
        {
            return null;
        }

        return new CompletionEdit(new DocumentRange(new DocumentOffset(lineStart + start), new DocumentOffset(lineStart + start + length)),
            item.InsertText + (item.TakesOperand ? " " : ""), item.CaretOffset);
    }

    /// <summary>
    /// Returns a ghost suffix only when accepting the complete edit would append at an unselected line end.
    /// </summary>
    /// <param name="state">The prompt.</param>
    /// <returns>The appended suffix, or null when the edit would replace existing text.</returns>
    public string? Prediction(PromptState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Editor.Cursor.HasSelection || state.CaretColumn != state.CurrentLine.Length
            || Range.End != state.Editor.Cursor.Position)
        {
            return null;
        }

        var typed = state.Editor.Document.GetText(Range);
        return Text.Length > typed.Length && Text.StartsWith(typed, StringComparison.Ordinal) ? Text[typed.Length..] : null;
    }

    /// <summary>
    /// Replaces the range and records its text and caret together in one undo group.
    /// </summary>
    /// <param name="state">The prompt whose current document contains the range.</param>
    public void Apply(PromptState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var editor = state.Editor;
        var document = editor.Document;
        var operation = new ReplaceOperation(Range, Text);
        var inverse = new ReplaceOperation(new DocumentRange(Range.Start, new DocumentOffset(Range.Start.Value + Text.Length)),
            document.GetText(Range));
        var before = document.Version;
        editor.History.BeginGroup(editor.Cursors, before);
        document.Apply(operation, "completion");
        editor.History.RecordEdit(operation, inverse, editor.Cursors, before, document.Version);
        editor.SetCursorPosition(new DocumentOffset(Math.Clamp(Range.Start.Value + (CaretOffset ?? Text.Length), 0, document.Length)));
        editor.History.CommitGroup(editor.Cursors, document.Version);
        state.LastLength = document.Length;
    }
}
