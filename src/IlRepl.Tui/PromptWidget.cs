using Hex1b;
using Hex1b.Composition;
using Hex1b.Documents;
using Hex1b.Input;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// The prompt: an editor that holds one line or a whole block, with the prompt in its gutter, a
/// completion palette above it, ghost text for the best match, and history on Up and Down.
/// Enter is the only key that decides: it continues a buffer whose braces are open and submits
/// one whose braces balance, and while the palette has been moved through it accepts instead.
/// Every binding reads the state when the key arrives, not when the frame was built, so keys
/// that come in faster than frames still do the right thing. While a submission is in flight the
/// sent text is with the worker, not in the editor, so typing goes on; a buffer submitted then
/// goes after the one in flight, and Enter on an empty buffer does nothing.
/// </summary>
/// <param name="Label">The prompt shown on the first line.</param>
/// <param name="Catalog">Every opcode and command the palette can offer.</param>
/// <param name="State">The prompt's state, owned above the widget.</param>
/// <param name="Fit">How many rows the editor and the palette get.</param>
/// <param name="OpenDepth">How many closing braces the engine is already waiting for.</param>
/// <param name="CommentOpen">Whether the engine has a <c>/*</c> open when the buffer starts.</param>
public sealed record PromptWidget(string Label, IReadOnlyList<CompletionItem> Catalog, PromptState State, PromptFit Fit, int OpenDepth, bool CommentOpen) : Hex1bWidget
{
    internal Action<string>? SubmitHandler { get; init; }

    internal Action<string>? CopyHandler { get; init; }

    /// <summary>
    /// Sets the handler that receives a complete buffer.
    /// </summary>
    /// <param name="handler">The handler.</param>
    /// <returns>A copy of the widget with the handler set.</returns>
    public PromptWidget OnSubmit(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return this with { SubmitHandler = handler };
    }

    /// <summary>
    /// Sets the handler that copies a selection.
    /// </summary>
    /// <param name="handler">The handler.</param>
    /// <returns>A copy of the widget with the handler set.</returns>
    public PromptWidget OnCopy(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return this with { CopyHandler = handler };
    }

    /// <summary>
    /// What Enter does now: accepts a completion the user moved to, continues an open buffer,
    /// and submits a complete one. While lines are in flight a submission waits its turn, so
    /// nothing typed is lost and nothing runs out of order.
    /// </summary>
    /// <param name="state">The prompt's state.</param>
    /// <param name="paletteVisible">Whether the palette is showing.</param>
    /// <param name="openDepth">How many closing braces the engine is already waiting for.</param>
    /// <param name="commentOpen">Whether the engine has a <c>/*</c> open when the buffer starts.</param>
    /// <returns>The action.</returns>
    public static EnterAction EnterActionFor(PromptState state, bool paletteVisible, int openDepth, bool commentOpen)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (paletteVisible && state.PaletteNavigated)
        {
            return EnterAction.AcceptCompletion;
        }

        return BlockBalance.IsComplete(state.Text, openDepth, commentOpen) ? EnterAction.Submit : EnterAction.Continue;
    }

    /// <summary>
    /// The palette's candidates: the caret's line is one word, the caret is at its end, and the
    /// palette was not dismissed for it. An exact single match hides the palette.
    /// </summary>
    /// <param name="state">The prompt's state.</param>
    /// <param name="catalog">The catalog.</param>
    /// <returns>The candidates.</returns>
    public static IReadOnlyList<CompletionItem> Candidates(PromptState state, IReadOnlyList<CompletionItem> catalog)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        if (state.PaletteDismissed)
        {
            return [];
        }

        // The palette completes the first word while the caret sits right after it; a space
        // after the word moves the caret into the operand, where Up, Down, and Tab are the
        // buffer's again.
        var line = state.CurrentLine;
        var word = FirstWord(line);
        var start = line.Length - line.TrimStart().Length;
        if (word.Length == 0 || word != line.Trim() || state.CaretColumn != start + word.Length)
        {
            return [];
        }

        var candidates = CatalogCompleter.Complete(catalog, word);
        return candidates.Count == 1 && candidates[0].Name == word ? [] : candidates;
    }

    /// <summary>
    /// Builds the editor and, when there are candidates and room for them, the palette above it.
    /// </summary>
    /// <param name="ctx">The composition context.</param>
    /// <returns>The widget tree.</returns>
    protected override Hex1bWidget Build(CompositionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var state = State;
        state.View.Label = Label;
        state.Highlighter.CommentOpenAtStart = CommentOpen;
        state.Highlighter.Caret = new DocumentPosition(state.CaretLine, state.CaretColumn + 1);
        var candidates = Candidates(state, Catalog);
        var paletteVisible = candidates.Count > 0 && Fit.PaletteRows > 0;
        state.SelectedIndex = paletteVisible ? Math.Clamp(state.SelectedIndex, 0, candidates.Count - 1) : 0;
        var prediction = PredictionFor(state, Catalog);
        if (prediction is null)
        {
            state.Prediction.Hide();
        }
        else
        {
            state.Prediction.Show(new DocumentPosition(state.CaretLine, state.CurrentLine.Length + 1), prediction);
        }

        return ctx.VStack(v =>
        {
            var editor = v.Editor(state.Editor)
                .ViewRenderer(state.View)
                .WordWrap()
                .Decorations(state.Highlighter)
                .FixedHeight(Math.Max(1, Fit.EditorRows))
                .OnTextChanged(_ => TextChanged(state))
                .InputBindings(b => Bind(b, state));
            // The stack measures the paste wrapper, so the wrapper carries the height as well.
            var pastable = v.Pastable(editor).OnPaste(async e =>
            {
                var text = await e.Paste.ReadToEndAsync(ct: e.Paste.CancellationToken).ConfigureAwait(false);
                state.Post(SubmissionEvent.Paste(text));
            }).FixedHeight(Math.Max(1, Fit.EditorRows));
            return paletteVisible ? [BuildPalette(v, candidates, state, Fit.PaletteRows), pastable] : [pastable];
        });
    }

    private static string FirstWord(string text)
    {
        var trimmed = text.TrimStart();
        var space = trimmed.IndexOfAny([' ', '\t']);
        return space < 0 ? trimmed : trimmed[..space];
    }

    private static string? PredictionFor(PromptState state, IReadOnlyList<CompletionItem> catalog)
    {
        if (state.PaletteDismissed)
        {
            return null;
        }

        var line = state.CurrentLine;
        var typed = FirstWord(line);
        if (typed.Length == 0 || typed != line.TrimStart() || state.CaretColumn < line.Length)
        {
            return null;
        }

        var matches = CatalogCompleter.Complete(catalog, typed);
        var best = matches.Count > 0 ? matches[0] : null;
        return best is null || best.Name == typed ? null : best.Name[typed.Length..];
    }

    private void Bind(InputBindingsBuilder b, PromptState state)
    {
        // The builder runs for every key, so what it reads here is the state that key meets.
        var candidates = Candidates(state, Catalog);
        var paletteVisible = candidates.Count > 0 && Fit.PaletteRows > 0;
        var predictionVisible = PredictionFor(state, Catalog) is not null;
        var onFirst = state.CaretLine <= 1;
        var onLast = state.CaretLine >= state.LineCount;

        b.Remove(EditorWidget.InsertNewline);
        b.Key(Hex1bKey.Enter).Action(_ => Enter(state, candidates, EnterActionFor(state, paletteVisible, OpenDepth, CommentOpen)), "Send or continue");
        b.Remove(EditorWidget.InsertTab);
        b.Key(Hex1bKey.Tab).Action(_ => Tab(state, candidates, paletteVisible), "Complete or indent");
        b.Remove(Hex1bKey.Escape);
        b.Key(Hex1bKey.Escape).Action(_ =>
        {
            state.PaletteDismissed = true;
            state.PaletteNavigated = false;
        }, "Dismiss palette");
        b.Remove(EditorWidget.AddCursorAtNextMatch);
        b.Remove(EditorWidget.CtrlClick);
        b.Remove(EditorWidget.ToggleFold);
        if (state.Text.Length == 0)
        {
            // With nothing to select, Shift+Up reaches the transcript through the root's binding.
            b.Remove(EditorWidget.SelectUp);
            b.Remove(EditorWidget.SelectDown);
        }

        if (paletteVisible || onFirst)
        {
            b.Remove(EditorWidget.MoveUp);
            b.Key(Hex1bKey.UpArrow).Action(_ =>
            {
                if (paletteVisible)
                {
                    state.SelectedIndex = Math.Max(0, state.SelectedIndex - 1);
                    state.PaletteNavigated = true;
                }
                else
                {
                    HistoryBack(state);
                }
            }, "Previous");
        }

        if (paletteVisible || onLast)
        {
            b.Remove(EditorWidget.MoveDown);
            b.Key(Hex1bKey.DownArrow).Action(_ =>
            {
                if (paletteVisible)
                {
                    state.SelectedIndex = Math.Min(candidates.Count - 1, state.SelectedIndex + 1);
                    state.PaletteNavigated = true;
                }
                else
                {
                    HistoryForward(state);
                }
            }, "Next");
        }

        if (predictionVisible)
        {
            b.Remove(EditorWidget.MoveRight);
            b.Key(Hex1bKey.RightArrow).Action(_ => AcceptPrediction(state), "Accept prediction");
        }

        b.Ctrl().Key(Hex1bKey.P).Action(_ => HistoryBack(state), "Previous entry");
        b.Ctrl().Key(Hex1bKey.N).Action(_ => HistoryForward(state), "Next entry");
        b.Ctrl().Key(Hex1bKey.U).Action(_ => DeleteToLineStart(state), "Delete to the start of the line");
        b.Ctrl().Key(Hex1bKey.C).Action(c => CtrlC(state, c), "Copy, clear, or quit");
    }

    private void Enter(PromptState state, IReadOnlyList<CompletionItem> candidates, EnterAction enter)
    {
        switch (enter)
        {
            case EnterAction.AcceptCompletion:
                Accept(state, candidates);
                return;
            case EnterAction.Continue:
                state.Editor.InsertText("\n" + AutoIndent.Continuation(state.TextBeforeCaret, state.CommentOpenBefore(state.CaretLine)));
                state.LastLength = state.Editor.Document.Length;
                state.PaletteDismissed = false;
                state.PaletteNavigated = false;
                return;
            default:
                var text = state.Text;
                state.Clear();
                state.PaletteDismissed = false;
                state.PaletteNavigated = false;
                state.Prediction.Hide();
                SubmitHandler?.Invoke(text);
                return;
        }
    }

    private static void Tab(PromptState state, IReadOnlyList<CompletionItem> candidates, bool paletteVisible)
    {
        if (paletteVisible)
        {
            Accept(state, candidates);
        }
        else if (state.LineCount > 1 && state.CurrentLine.Trim().Length == 0)
        {
            state.Editor.InsertText(AutoIndent.Unit);
            state.LastLength = state.Editor.Document.Length;
        }
    }

    private static void Accept(PromptState state, IReadOnlyList<CompletionItem> candidates)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        var item = candidates[Math.Clamp(state.SelectedIndex, 0, candidates.Count - 1)];
        var completed = AutoIndent.LeadingWhitespace(state.CurrentLine) + item.Name + (item.TakesOperand ? " " : "");
        ReplaceCurrentLine(state, completed);
        state.PaletteDismissed = true;
        state.PaletteNavigated = false;
    }

    private static void AcceptPrediction(PromptState state)
    {
        var suffix = state.Prediction.Suffix;
        if (suffix is null)
        {
            return;
        }

        state.Editor.SetCursorPosition(new DocumentOffset(LineEnd(state)));
        state.Editor.InsertText(suffix);
        state.LastLength = state.Editor.Document.Length;
        state.PaletteDismissed = true;
        state.Prediction.Hide();
    }

    private void CtrlC(PromptState state, InputBindingActionContext context)
    {
        if (state.Editor.Cursor.HasSelection && !state.SelectionIsReturned)
        {
            CopyHandler?.Invoke(state.Editor.Document.GetText(state.Editor.Cursor.SelectionRange));
            return;
        }

        if (state.Busy)
        {
            // What was queued behind the submission stays queued: it comes back to the editor
            // with the withdrawn text once the cancel lands.
            state.Submission?.Cancel();
            return;
        }

        if (state.Text.Length > 0)
        {
            state.Clear();
            state.History.Reset();
            state.PaletteDismissed = false;
            state.PaletteNavigated = false;
            state.Prediction.Hide();
            return;
        }

        context.RequestStop();
    }

    // Ctrl+U as readline and prompt_toolkit have it: the line is cut from the caret back to
    // its start, and at the start of a line the line break before the caret goes, so the caret
    // lands at the end of the line above with the rest of this one following it. Each is one
    // edit that undo puts back, caret included.
    private static void DeleteToLineStart(PromptState state)
    {
        var editor = state.Editor;
        var document = editor.Document;
        var caret = editor.Cursor.Position;
        var position = document.OffsetToPosition(caret);
        var start = document.PositionToOffset(new DocumentPosition(position.Line, 1));
        DocumentRange cut;
        if (start.Value < caret.Value)
        {
            cut = new DocumentRange(start, caret);
        }
        else if (position.Line > 1)
        {
            cut = new DocumentRange(new DocumentOffset(start.Value - 1), start);
        }
        else
        {
            return;
        }

        Replace(editor, cut, "");
        state.LastLength = document.Length;
        state.PaletteDismissed = false;
        state.PaletteNavigated = false;
    }

    private static void HistoryBack(PromptState state)
    {
        var text = state.History.Back(state.Text);
        if (text is not null)
        {
            Recall(state, text);
        }
    }

    private static void HistoryForward(PromptState state)
    {
        var text = state.History.Forward(state.Text);
        if (text is not null)
        {
            Recall(state, text);
        }
    }

    private static void Recall(PromptState state, string text)
    {
        state.SetText(text, text.Length);
        state.PaletteDismissed = true;
        state.PaletteNavigated = false;
        state.Prediction.Hide();
    }

    private static void ReplaceCurrentLine(PromptState state, string text)
    {
        var document = state.Editor.Document;
        var start = document.PositionToOffset(new DocumentPosition(state.CaretLine, 1));
        state.Editor.SetCursorPosition(start);
        state.Editor.SetCursorPosition(new DocumentOffset(LineEnd(state)), extend: true);
        state.Editor.InsertText(text);
        state.LastLength = document.Length;
    }

    private static int LineEnd(PromptState state)
    {
        var document = state.Editor.Document;
        return document.PositionToOffset(new DocumentPosition(state.CaretLine, 1)).Value + state.CurrentLine.Length;
    }

    private static void TextChanged(PromptState state)
    {
        state.PaletteDismissed = false;
        state.PaletteNavigated = false;
        state.SelectedIndex = 0;
        var editor = state.Editor;
        var length = editor.Document.Length;
        if (length == state.LastLength + 1 && state.CaretColumn > 0)
        {
            // A closing brace typed on an indented blank line steps the line out. The brace's own
            // edit is undone and replaced by one recorded edit that swaps the indentation for the
            // dedented brace, so one undo brings the blank line back as it was, caret included.
            var before = state.TextBeforeCaret;
            var indentation = before[..^1];
            if (before.EndsWith('}') && AutoIndent.ShouldDedent(indentation, state.CommentOpenBefore(state.CaretLine)))
            {
                var dedented = AutoIndent.Dedent(indentation);
                if (dedented.Length < indentation.Length)
                {
                    editor.Undo();
                    var document = editor.Document;
                    var caret = editor.Cursor.Position;
                    var lineStart = document.PositionToOffset(new DocumentPosition(document.OffsetToPosition(caret).Line, 1));
                    Replace(editor, new DocumentRange(lineStart, caret), dedented + "}");
                }
            }
        }

        state.LastLength = editor.Document.Length;
    }

    // One recorded edit that swaps a range for new text and puts the caret after it, so undo
    // brings back the text and the caret as they were, with no selection left behind.
    private static void Replace(EditorState editor, DocumentRange range, string replacement)
    {
        var document = editor.Document;
        var operation = new ReplaceOperation(range, replacement);
        var inverse = new ReplaceOperation(new DocumentRange(range.Start, new DocumentOffset(range.Start.Value + replacement.Length)), document.GetText(range));
        var versionBefore = document.Version;
        editor.History.BeginGroup(editor.Cursors, versionBefore);
        document.Apply(operation, "prompt");
        editor.History.RecordEdit(operation, inverse, editor.Cursors, versionBefore, document.Version);
        editor.SetCursorPosition(new DocumentOffset(range.Start.Value + replacement.Length));
        editor.History.CommitGroup(editor.Cursors, document.Version);
    }

    private static BorderWidget BuildPalette(WidgetContext<VStackWidget> context, IReadOnlyList<CompletionItem> candidates, PromptState state, int paletteRows)
    {
        var rows = Math.Max(1, paletteRows);
        var first = Math.Clamp(state.SelectedIndex - (rows / 2), 0, Math.Max(0, candidates.Count - rows));
        var visible = candidates.Skip(first).Take(rows).ToList();
        // Column widths come from every candidate, not just the visible ones, so the columns stay
        // put while the selection scrolls through the list.
        var nameWidth = Math.Max(12, candidates.Max(c => c.Name.Length) + 2);
        var detailWidth = Math.Max(4, candidates.Max(c => c.Detail.Length) + 2);

        var lines = visible.Select((item, offset) =>
        {
            var index = first + offset;
            var selected = index == state.SelectedIndex;
            var marker = selected ? " ❯ " : "   ";
            var line = marker + item.Name.PadRight(nameWidth) + item.Detail.PadRight(detailWidth) + item.Description;
            return (Hex1bWidget)context.Interactable(ic => ic.ThemePanel(SpanPalette.Mutator(selected ? SpanStyle.TopType : SpanStyle.Opcode), ic.Text(line)))
                .OnHoverChanged(args =>
                {
                    if (args.IsHovered)
                    {
                        state.SelectedIndex = index;
                        state.PaletteNavigated = true;
                    }
                })
                .OnClick(_ => Accept(state, candidates));
        }).ToArray();

        var kind = candidates[0].Name.StartsWith('.') ? "commands" : "opcodes";
        var title = candidates.Count > rows ? $"{kind} {state.SelectedIndex + 1}/{candidates.Count}" : kind;
        return context.Border(b => lines).Title(title);
    }
}
