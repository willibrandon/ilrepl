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
    /// <returns>The same builder, for chaining.</returns>
    public static Hex1bTerminalBuilder Configure(Hex1bTerminalBuilder builder, IReplEngine engine, Transcript transcript, bool usePlatformClipboard = false)
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
        return builder
            .AddPresentationFilter(size)
            .AddPresentationFilter(new PromptCursorFilter())
            .WithHex1bApp(
                // Each input event is handled and rendered on its own, so focus pulled back to the
                // prompt by one render is in place before the next key is read.
                options => options.EnableInputCoalescing = false,
                app =>
                {
                    app.RequestFocus(node => node is TextBoxNode);
                    size.Changed += app.Invalidate;
                    return ctx => BuildRoot(ctx, app, engine, transcript, size, feedback, usePlatformClipboard);
                });
    }

    /// <summary>
    /// Runs the REPL on the current console until the user leaves.
    /// </summary>
    /// <param name="engine">The engine that handles lines.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(IReplEngine engine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var transcript = new Transcript { MaxLines = 1000 };
        await using var terminal = Configure(Hex1bTerminal.CreateBuilder(), engine, transcript, usePlatformClipboard: true)
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
    public static IReadOnlyList<string> StatusHints(IReadOnlyList<string> occupied, int width, bool copyMode)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        var hints = copyMode
            ? new List<string> { "Shift+↑↓ extend", "y yank", "Esc cancel" }
            : new List<string> { "Tab complete", "Shift+↑ select", "Ctrl+Q quit" };
        if (width <= 0)
        {
            return hints;
        }

        static int Width(IReadOnlyList<string> sections) => sections.Sum(DisplayWidth.GetStringWidth) + (3 * Math.Max(0, sections.Count - 1));
        while (hints.Count > 1 && Width(occupied) + 2 + Width(hints) > width)
        {
            hints.RemoveAt(0);
        }

        return hints;
    }

    /// <summary>
    /// Finds the transcript's selection panel node by walking up from the focused node and back
    /// down the tree, or null before the first frame.
    /// </summary>
    /// <param name="app">The running app.</param>
    /// <returns>The panel node, or null.</returns>
    public static SelectionPanelNode? FindSelectionPanel(Hex1bApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var root = app.FocusedNode;
        while (root?.Parent is not null)
        {
            root = root.Parent;
        }

        return root is null ? null : Descend(root);

        static SelectionPanelNode? Descend(Hex1bNode node)
        {
            if (node is SelectionPanelNode panel)
            {
                return panel;
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

    private static VStackWidget BuildRoot(RootContext ctx, Hex1bApp app, IReplEngine engine, Transcript transcript, TerminalSizeFilter size, YankFeedback feedback, bool usePlatformClipboard)
    {
        // Any mouse event, a wheel notch included, focuses the node under the pointer. The prompt
        // is the only place input goes, so focus is pulled back on the next render.
        if (app.FocusedNode is not null and not TextBoxNode)
        {
            app.RequestFocus(node => node is TextBoxNode);
        }

        var status = engine.Status;
        var panel = FindSelectionPanel(app);
        var copyMode = panel?.IsInCopyMode == true;
        // The scrollbar takes the last column of the transcript panel.
        var lineWidth = size.Width > 1 ? size.Width - 1 : 0;
        return ctx.VStack(v =>
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
                .Follow()
                .Fill(),
            v.Separator(),
            v.IlPrompt(status.Prompt, engine.Catalog)
                .OnSubmit(async line =>
                {
                    try
                    {
                        var reply = await engine.HandleAsync(line, CancellationToken.None).ConfigureAwait(true);
                        foreach (var l in reply.Lines)
                        {
                            transcript.Add(l);
                        }

                        if (reply.Quit)
                        {
                            app.RequestStop();
                        }
                    }
                    catch (ReplEngineException ex)
                    {
                        transcript.Add(new TranscriptLine(LineKind.Error, [new TranscriptSpan("  engine error: ", SpanStyle.Error), new TranscriptSpan(ex.Message)]));
                    }

                    app.RequestFocus(node => node is TextBoxNode);
                    app.Invalidate();
                }),
            v.InfoBar(s =>
            {
                var facts = new List<string>
                {
                    "stack " + status.Stack,
                    status.Locals == 0 ? "no locals" : $"{status.Locals} local{(status.Locals == 1 ? "" : "s")}",
                    status.OpenBlocks > 0 ? $"{status.OpenBlocks} open block{(status.OpenBlocks == 1 ? "" : "s")}" : $"{status.Instructions} instruction{(status.Instructions == 1 ? "" : "s")}",
                };
                var occupied = feedback.Notification is null ? facts : [.. facts, feedback.Notification];
                var hints = StatusHints(occupied, size.Width, copyMode);
                var children = new List<IInfoBarChild>();
                children.AddRange(facts.Select(f => (IInfoBarChild)s.Section(f)));
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
                if (FindSelectionPanel(app) is { IsInCopyMode: false } target)
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
    }
}
