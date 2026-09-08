using Hex1b;
using Hex1b.Input;
using Hex1b.Nodes;
using Hex1b.Theming;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// The terminal UI: a following transcript, the prompt, and a status bar. The layout is
/// attached to a terminal builder so the same tree runs on a real console or a headless
/// emulator in tests.
/// </summary>
public static class IlReplApp
{
    /// <summary>
    /// The banner shown at the top of an empty transcript.
    /// </summary>
    public const string Banner = "ilrepl  type IL, watch the stack, ret runs the cell.  .help for more";

    /// <summary>
    /// Attaches the REPL application to a terminal builder.
    /// </summary>
    /// <param name="builder">The terminal builder.</param>
    /// <param name="engine">The engine that handles lines.</param>
    /// <param name="transcript">The transcript to render and append to.</param>
    /// <param name="usePlatformClipboard">True to copy selections through the platform's clipboard command as well as the terminal.</param>
    /// <param name="onApp">Called once the app exists, before its first frame.</param>
    /// <param name="history">Where history is kept between runs, or null to keep it for this run only.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static Hex1bTerminalBuilder Configure(Hex1bTerminalBuilder builder, IReplEngine engine, Transcript transcript, bool usePlatformClipboard = false, Action<Hex1bApp>? onApp = null, IHistoryStore? history = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(transcript);
        if (transcript.Lines.Count == 0)
        {
            transcript.Add(LineKind.Info, Banner, SpanStyle.Dim);
        }

        // The terminal reports its size to this filter, and long lines are folded at that width.
        var size = new TerminalSizeFilter();
        var feedback = new YankFeedback();
        var prompt = new PromptState(new PromptHistory(history), new CilTokenizer(engine.Vocabulary));
        return builder
            .AddPresentationFilter(size)
            .WithHex1bApp(
                options =>
                {
                    // The prompt paints its own caret cell, so no hardware caret follows the mouse,
                    // and Ctrl+C is the prompt's: it copies, clears, or quits.
                    options.EnableDefaultCtrlCExit = false;
                    options.Theme = Hex1bThemes.Default.Clone()
                        .Set(MouseTheme.ShowCursor, false)
                        .Set(EditorTheme.CursorBackgroundColor, SpanPalette.Color(SpanStyle.Prompt));
                },
                app =>
                {
                    onApp?.Invoke(app);
                    prompt.Invalidate = app.Invalidate;
                    app.RequestFocus(node => node is EditorNode);
                    size.Changed += app.Invalidate;
                    if (history is not null)
                    {
                        _ = LoadHistoryAsync(history, prompt);
                    }

                    return ctx => BuildRoot(ctx, app, engine, transcript, size, feedback, prompt, usePlatformClipboard);
                });
    }

    /// <summary>
    /// Runs the REPL on the current console until the user leaves.
    /// </summary>
    /// <param name="engine">The engine that handles lines.</param>
    /// <param name="history">Where history is kept between runs, or null to keep it for this run only.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(IReplEngine engine, IHistoryStore? history, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var transcript = new Transcript { MaxLines = 1000 };
        await using var terminal = Configure(Hex1bTerminal.CreateBuilder(), engine, transcript, usePlatformClipboard: true, history: history)
            .WithMouse()
            .Build();
        return await terminal.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The key hints for the status bar: what copy mode offers while it is active, otherwise the
    /// everyday keys. Hints are dropped from the left while they do not fit beside the other
    /// sections, so a long stack never clips the one that matters most.
    /// </summary>
    /// <param name="occupied">The sections already on the bar.</param>
    /// <param name="width">The terminal width, or zero when not known yet.</param>
    /// <param name="copyMode">True while the transcript is in copy mode.</param>
    /// <returns>The hints that fit.</returns>
    public static IReadOnlyList<string> StatusHints(IReadOnlyList<string> occupied, int width, bool copyMode) =>
        StatusHints(occupied, width, copyMode, EnterAction.Submit, 1);

    private static int Width(IReadOnlyList<string> sections) => sections.Sum(DisplayWidth.GetStringWidth) + (3 * Math.Max(0, sections.Count - 1));

    /// <summary>
    /// The key hints for the status bar, read from what Enter will do now, so the hint never
    /// promises one thing while the key does another. The least important hint comes first,
    /// because hints are dropped from the left when the bar is too narrow for them all.
    /// </summary>
    /// <param name="occupied">The sections already on the bar.</param>
    /// <param name="width">The terminal width, or zero when not known yet.</param>
    /// <param name="copyMode">True while the transcript is in copy mode.</param>
    /// <param name="enter">What Enter will do.</param>
    /// <param name="lineCount">How many lines the buffer has.</param>
    /// <returns>The hints that fit.</returns>
    public static IReadOnlyList<string> StatusHints(IReadOnlyList<string> occupied, int width, bool copyMode, EnterAction enter, int lineCount)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        var hints = copyMode
            ? new List<string> { "Shift+↑↓ extend", "y yank", "Esc cancel" }
            : enter switch
            {
                EnterAction.Busy => new List<string> { "Ctrl+C cancels", "Ctrl+Q quit" },
                EnterAction.Continue => new List<string> { "Ctrl+C clears", "Enter continues", "Ctrl+Q quit" },
                EnterAction.AcceptCompletion => new List<string> { "Esc dismiss", "Enter accepts", "Ctrl+Q quit" },
                _ when lineCount > 1 => new List<string> { "Ctrl+C clears", $"Enter sends {lineCount} lines", "Ctrl+Q quit" },
                _ => new List<string> { "Tab complete", "Shift+↑ select", "Ctrl+Q quit" },
            };
        if (width <= 0)
        {
            return hints;
        }

        while (hints.Count > 1 && Width(occupied) + 2 + Width(hints) > width)
        {
            hints.RemoveAt(0);
        }

        return hints;
    }

    /// <summary>
    /// Finds the first node of a type by walking up from the focused node to the root and back
    /// down the tree, or null before the first frame.
    /// </summary>
    /// <typeparam name="TNode">The node type to find.</typeparam>
    /// <param name="app">The running app.</param>
    /// <returns>The node, or null.</returns>
    public static TNode? FindNode<TNode>(Hex1bApp app)
        where TNode : Hex1bNode
    {
        ArgumentNullException.ThrowIfNull(app);
        var root = app.FocusedNode;
        while (root?.Parent is not null)
        {
            root = root.Parent;
        }

        return root is null ? null : Descend(root);

        static TNode? Descend(Hex1bNode node)
        {
            if (node is TNode match)
            {
                return match;
            }

            foreach (var child in node.GetChildren())
            {
                if (Descend(child) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    private static void Scroll(Hex1bApp app, int amount, InputBindingActionContext context)
    {
        FindNode<ScrollPanelNode>(app)?.ScrollBy(amount);
        context.FocusWhere(node => node is EditorNode);
        context.Invalidate();
    }

    private static DragHandler SelectionDrag(Hex1bApp app, SelectionMode mode)
    {
        app.FocusWhere(node => node is EditorNode);
        var panel = FindNode<SelectionPanelNode>(app);
        if (panel is null)
        {
            return new DragHandler(onMove: (_, _, _) => { }, onEnd: _ => { });
        }

        // Copy mode starts on the first movement, with the anchor where the press was, so a plain
        // click selects nothing. Releasing keeps the selection so y can yank it.
        var started = false;
        return new DragHandler(
            onMove: (context, dx, dy) =>
            {
                var row = context.MouseY - panel.Bounds.Y;
                var column = context.MouseX - panel.Bounds.X;
                if (!started)
                {
                    started = true;
                    panel.EnterCopyMode();
                    panel.SetCursor(row - dy, column - dx);
                    panel.StartOrToggleSelection(mode);
                    context.CaptureInput(panel);
                }

                panel.SetCursor(row, column);
                context.Invalidate();
            },
            onEnd: context => context.Invalidate());
    }

    private static async Task LoadHistoryAsync(IHistoryStore store, PromptState prompt)
    {
        var entries = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        prompt.Post(SubmissionEvent.HistoryLoaded(entries));
    }

    private static void StartSubmission(PromptState prompt, IReplEngine engine, string text)
    {
        if (prompt.Busy)
        {
            // Sent after the one in flight; nothing typed is lost and nothing runs out of order.
            prompt.Pending.Enqueue(text);
            return;
        }

        var status = engine.Status;
        var isNew = prompt.History.Add(text);
        var lines = text.Split('\n');
        prompt.Submission = new Submission(engine, lines, status.OpenDepth, status.Mark.InBlockComment,
            ct => isNew ? prompt.History.PersistAsync(text, ct) : Task.CompletedTask, prompt.Post);
    }

    private static void StartPending(PromptState prompt, IReplEngine engine)
    {
        prompt.Submission = null;
        if (prompt.Pending.TryDequeue(out var next))
        {
            StartSubmission(prompt, engine, next);
        }
    }

    private static void Drain(PromptState prompt, Transcript transcript, IReplEngine engine, Hex1bApp app)
    {
        while (prompt.Events.TryDequeue(out var e))
        {
            foreach (var line in e.Lines ?? [])
            {
                transcript.Add(line);
            }

            switch (e.Kind)
            {
                case SubmissionEventKind.Paste:
                    prompt.Editor.InsertText(PastePayload.Prepare(e.Text ?? ""));
                    prompt.LastLength = prompt.Editor.Document.Length;
                    prompt.PaletteDismissed = false;
                    prompt.PaletteNavigated = false;
                    break;
                case SubmissionEventKind.Refused:
                    Return(prompt, e.Text ?? "", e.CaretLine, select: true);
                    if (e.Note is { } note)
                    {
                        transcript.Add(LineKind.Info, "  " + note, SpanStyle.Dim);
                    }

                    prompt.Submission = null;
                    break;
                case SubmissionEventKind.Cancelled:
                    Return(prompt, e.Text ?? "", 0, select: false);
                    prompt.Submission = null;
                    break;
                case SubmissionEventKind.Failed:
                    if (e.Note is { } message)
                    {
                        transcript.Add(new TranscriptLine(LineKind.Error, [new TranscriptSpan("  engine error: ", SpanStyle.Error), new TranscriptSpan(message)]));
                    }

                    Return(prompt, e.Text ?? "", 0, select: false);
                    prompt.Submission = null;
                    break;
                case SubmissionEventKind.Quit:
                    app.RequestStop();
                    break;
                case SubmissionEventKind.Completed:
                    StartPending(prompt, engine);
                    break;
                case SubmissionEventKind.HistoryLoaded:
                    prompt.History.Replace(e.Entries ?? []);
                    break;
                default:
                    break;
            }
        }

        if (prompt.History.Problem is { } problem && !prompt.HistoryProblemShown)
        {
            prompt.HistoryProblemShown = true;
            transcript.Add(LineKind.Info, "  history is not being saved: " + problem, SpanStyle.Dim);
        }
    }

    private static void Return(PromptState prompt, string text, int caretLine, bool select)
    {
        // What was queued behind the submission and what has been typed since come back too,
        // after the returned lines, so nothing is lost.
        var rest = string.Join('\n', prompt.Pending.Append(prompt.Text).Where(t => t.Length > 0));
        prompt.Pending.Clear();
        if (text.Length == 0 && rest.Length == 0)
        {
            prompt.Clear();
            return;
        }

        text = text.Length == 0 ? rest : rest.Length == 0 ? text : text + "\n" + rest;
        var offset = 0;
        var lines = text.Split('\n');
        for (var i = 0; i < caretLine && i < lines.Length; i++)
        {
            offset += lines[i].Length + 1;
        }

        prompt.SetText(text, offset);
        prompt.PaletteDismissed = true;
        if (select)
        {
            prompt.SelectLine(caretLine);
        }
    }

    private static Hex1bWidget[] TranscriptLines(Transcript transcript, int lineWidth, YankFeedback feedback)
    {
        var widgets = new Hex1bWidget[transcript.Lines.Count];
        var row = 0;
        for (var i = 0; i < widgets.Length; i++)
        {
            var line = transcript.Lines[i];
            var flash = false;
            if (feedback.FlashTop >= 0)
            {
                // Only while something is flashing are the rows counted, to know which lines it covers.
                var rows = TranscriptLineFolder.Fold(line.Spans, lineWidth).Count;
                flash = feedback.Covers(row, rows);
                row += rows;
            }

            widgets[i] = new TranscriptLineWidget(line, lineWidth, flash);
        }

        return widgets;
    }

    private static VStackWidget BuildRoot(RootContext ctx, Hex1bApp app, IReplEngine engine, Transcript transcript, TerminalSizeFilter size, YankFeedback feedback, PromptState prompt, bool usePlatformClipboard)
    {
        // Whatever the submission worker and the paste handler posted since the last frame is
        // applied here, on the render thread, before anything reads the transcript or the prompt.
        Drain(prompt, transcript, engine, app);

        // The prompt is the only place input goes. A click on the scrollbar still focuses the
        // transcript panel, so focus is pulled back on the next render as a last resort; clicks
        // and wheel notches over the transcript itself are handed back at once, below.
        if (app.FocusedNode is not null and not EditorNode)
        {
            app.RequestFocus(node => node is EditorNode);
        }

        var status = engine.Status;
        var panel = FindNode<SelectionPanelNode>(app);
        var copyMode = panel?.IsInCopyMode == true;
        var candidates = PromptWidget.Candidates(prompt, engine.Catalog).Count;
        var fit = PromptLayout.Fit(size.Height, prompt.LineCount, candidates);
        if (fit.EditorRows != prompt.LastEditorRows)
        {
            // The transcript keeps its newest rows in view while the editor takes or gives back
            // rows: the panel snaps to its end when content grows, but not when its viewport
            // shrinks, so the end is asked for again on the frame after the rows change.
            prompt.LastEditorRows = fit.EditorRows;
            prompt.RowsChanged = true;
        }

        if (FindNode<ScrollPanelNode>(app) is { IsFollowing: true } following)
        {
            following.ScrollToBottom();
        }
        var enter = PromptWidget.EnterActionFor(prompt, candidates > 0 && fit.PaletteRows > 0, status.OpenDepth, status.Mark.InBlockComment);
        // The scrollbar takes the last column of the transcript panel.
        var lineWidth = size.Width > 1 ? size.Width - 1 : 0;
        var root = ctx.VStack(v =>
        [
            // The selection panel adds copy mode to the transcript: drag with the mouse or press
            // Shift+Up, then y copies the selection and the copied rows flash.
            v.VScrollPanel(sv =>
                [
                    sv.SelectionPanel(sv.VStack(lines => TranscriptLines(transcript, lineWidth, feedback)))
                        .OnCopy((SelectionPanelCopyEventArgs args) =>
                        {
                            ClipboardWriter.Copy(app, args.Text, usePlatformClipboard);
                            feedback.Show(app, args);
                        })
                        .InputBindings(b =>
                        {
                            // Shift+Up starts a selection from the prompt (a root binding below), so the
                            // panel's own entry key is not needed, and Shift keeps extending it.
                            b.Remove(SelectionPanelWidget.EnterCopyMode);
                            b.Shift().Key(Hex1bKey.UpArrow).OverridesCapture().Triggers(SelectionPanelWidget.CopyModeUp);
                            b.Shift().Key(Hex1bKey.DownArrow).OverridesCapture().Triggers(SelectionPanelWidget.CopyModeDown);
                        }),
                ], showScrollbar: true)
                .InputBindings(b =>
                {
                    // A press on the transcript focuses the panel before any binding runs. These
                    // bindings hand focus back to the prompt inside the same event, so keys that
                    // arrive right behind the mouse still reach it. A press that moves becomes a
                    // selection in the panel; the scrollbar's own drag has already had first pick.
                    b.Drag(MouseButton.Left).Action((x, y) => SelectionDrag(app, SelectionMode.Character), "Select");
                    b.Drag(MouseButton.Left).Ctrl().Action((x, y) => SelectionDrag(app, SelectionMode.Line), "Select lines");
                    b.Drag(MouseButton.Left).Alt().Action((x, y) => SelectionDrag(app, SelectionMode.Block), "Select a block");
                    b.Mouse(MouseButton.ScrollUp).Action(c => Scroll(app, -3, c), "Scroll up");
                    b.Mouse(MouseButton.ScrollDown).Action(c => Scroll(app, 3, c), "Scroll down");
                })
                .Follow()
                .Fill(),
            v.Separator(),
            v.IlPrompt(status.Prompt, engine.Catalog, prompt, fit, status.OpenDepth, status.Mark.InBlockComment)
                .OnSubmit(text => StartSubmission(prompt, engine, text))
                .OnCopy(text => ClipboardWriter.Copy(app, text, usePlatformClipboard)),
            v.InfoBar(s =>
            {
                var facts = new List<string>
                {
                    "stack " + status.Stack,
                    status.Locals == 0 ? "no locals" : $"{status.Locals} local{(status.Locals == 1 ? "" : "s")}",
                    status.OpenBlocks > 0 ? $"{status.OpenBlocks} open block{(status.OpenBlocks == 1 ? "" : "s")}" : $"{status.Instructions} instruction{(status.Instructions == 1 ? "" : "s")}",
                };
                if (prompt.Submission is { IsRunning: true } sending)
                {
                    facts.Add(sending.CancelRequested ? $"cancelling {sending.Sent}/{sending.Total}" : $"sending {sending.Sent}/{sending.Total}");
                }
                else if (prompt.LineCount > 1)
                {
                    facts.Add($"editing {prompt.LineCount} lines");
                }

                // The open blocks lead, because every fact after them describes the innermost one:
                // the class, then the method inside it.
                var leading = new List<(string Text, SpanStyle Style)>();
                if (status.OpenType is { } openType)
                {
                    leading.Add(("class " + openType, SpanStyle.Type));
                }

                if (status.OpenMethod is { } method)
                {
                    leading.Add(("method " + method, SpanStyle.Label));
                }

                facts.InsertRange(0, leading.Select(l => l.Text));
                var occupied = feedback.Notification is null ? facts : [.. facts, feedback.Notification];
                var hints = StatusHints(occupied, size.Width, copyMode, enter, prompt.LineCount);

                // When even the last hint does not fit beside the facts, the facts give way from
                // the left: the open block names first, then the stack; what the bar is doing now stays.
                var drop = 0;
                while (size.Width > 0 && facts.Count - drop > 1 && Width(facts.Skip(drop).ToList()) + 2 + Width(hints) > size.Width)
                {
                    drop++;
                }

                var children = new List<IInfoBarChild>();
                foreach (var (text, style) in leading.Skip(drop))
                {
                    children.Add(s.Section(text).Theme(t => t.Clone().Set(GlobalTheme.ForegroundColor, SpanPalette.Color(style))));
                }

                children.AddRange(facts.Skip(Math.Max(leading.Count, drop)).Select(f => (IInfoBarChild)s.Section(f)));

                children.Add(s.Spacer());
                if (feedback.Notification is { } note)
                {
                    children.Add(s.Section(note).Theme(t => t.Clone().Set(GlobalTheme.ForegroundColor, SpanPalette.Color(SpanStyle.String))));
                }

                children.AddRange(hints.Select(h => (IInfoBarChild)s.Section(h)));
                return children;
            }).Divider(" │ "),
        ])
        .InputBindings(b =>
        {
            // Shift+Up from the prompt selects the last transcript line; more Shift+Up extends it.
            b.Shift().Key(Hex1bKey.UpArrow).Action(c =>
            {
                if (FindNode<SelectionPanelNode>(app) is { IsInCopyMode: false } target)
                {
                    target.EnterCopyMode();
                    target.StartOrToggleSelection(SelectionMode.Line);
                    c.CaptureInput(target);
                    c.Invalidate();
                }
            }, "Select transcript lines");
            b.Ctrl().Key(Hex1bKey.Q).Action(c =>
            {
                c.RequestStop();
                return Task.CompletedTask;
            }, "Quit");
            b.Ctrl().Key(Hex1bKey.L).Action(_ =>
            {
                transcript.Clear();
                transcript.Add(LineKind.Info, Banner, SpanStyle.Dim);
                app.Invalidate();
            }, "Clear transcript");
        });

        // While lines are in flight the worker posts from another thread. A frame is asked for on
        // every post, and the root also redraws on a timer, so a post that lands while a frame is
        // already being drawn is still drained on the next one.
        var again = prompt.RowsChanged;
        prompt.RowsChanged = false;
        return prompt.Busy || !prompt.Events.IsEmpty || again ? root.RedrawAfter(16) : root;
    }
}
