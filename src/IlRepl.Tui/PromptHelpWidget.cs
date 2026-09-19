using Hex1b;
using Hex1b.Composition;
using Hex1b.Input;
using Hex1b.Theming;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Renders contextual help in the entire terminal viewport while the editor remains untouched.
/// </summary>
/// <param name="State">The retained prompt.</param>
/// <param name="Catalog">The opcode and command catalogue.</param>
/// <param name="Width">The terminal width.</param>
/// <param name="Height">The terminal height.</param>
public sealed record PromptHelpWidget(PromptState State, IReadOnlyList<CompletionItem> Catalog, int Width, int Height) : Hex1bWidget
{
    /// <summary>
    /// Builds a read-only, scrollable help surface with source and documentation actions.
    /// </summary>
    protected override Hex1bWidget Build(CompositionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var help = State.Help!;
        help.Refresh(State, Catalog);
        var width = Math.Max(1, Width);
        var indent = Math.Min(2, width - 1);
        var bodyWidth = width - indent;
        var height = Math.Max(1, Height - 3);
        var rows = help.Rows(bodyWidth);
        help.Scroll = Math.Clamp(help.Scroll, 0, Math.Max(0, rows.Count - height));
        var current = help.IsCurrent(State);
        var heading = help.Heading + (current ? "" : " · updating");
        var surface = ctx.Interactable(ic => ic.VStack(v =>
        {
            var content = new List<Hex1bWidget>
            {
                new TranscriptLineWidget(TranscriptLine.Of(LineKind.Info, PaletteText.Clip(heading, width), SpanStyle.Heading), width)
                    .CacheRendering().FixedHeight(1),
                v.Separator().FixedHeight(1),
            };
            foreach (var row in rows.Skip(help.Scroll).Take(height))
            {
                var action = row.Action >= 0 ? help.Actions[row.Action] : null;
                var actionCurrent = action is not null && help.IsActionCurrent(State, row.Action);
                var style = current || actionCurrent
                    ? row.Action == help.SelectedAction ? SpanStyle.TopType : SpanStyle.Member : SpanStyle.Dim;
                Hex1bWidget line = actionCurrent && action?.Url is { } url
                    ? v.ThemePanel(theme => theme.Clone()
                        .Set(HyperlinkTheme.ForegroundColor, SpanPalette.Color(style))
                        .Set(HyperlinkTheme.FocusedForegroundColor, SpanPalette.Color(style))
                        .Set(HyperlinkTheme.HoveredForegroundColor, SpanPalette.Color(style)),
                        v.Hyperlink(row.Line.PlainText, url).Id("ilrepl-help-" + row.Action).OnClick(_ =>
                    {
                        if (help.IsActionCurrent(State, row.Action) && help.Actions[row.Action] == action)
                        {
                            State.OpenDocumentation?.Invoke(url);
                        }
                    })) : new TranscriptLineWidget(current || actionCurrent ? row.Line
                        : row.Line with { Spans = row.Line.Spans.Select(span => span with { Style = SpanStyle.Dim }).ToArray() }, 0)
                        .CacheRendering();
                content.Add(v.HStack(h => [h.Text("").FixedWidth(indent), line.Fill()]).FixedHeight(1));
            }

            content.Add(v.Text("").Fill());
            content.Add(new TranscriptLineWidget(TranscriptLine.Of(LineKind.Info,
                PaletteText.Clip("Esc back · PgUp/PgDn scroll · Tab target · Enter open · F8 next", width), SpanStyle.Dim), width)
                .CacheRendering().FixedHeight(1));
            return [.. content];
        })).InputBindings(b => Bind(b, State, Catalog, bodyWidth, height));
        return ctx.Pastable(surface).OnPaste(e =>
        {
            e.Paste.Cancel();
            State.PasteInput?.Applied();
        });
    }

    /// <summary>
    /// Routes each key using the current help state, including keys queued before the next frame.
    /// </summary>
    internal static void Bind(InputBindingsBuilder b, PromptState state, IReadOnlyList<CompletionItem> catalog, int width, int height)
    {
        b.RemoveAll();
        b.Key(Hex1bKey.F1).Action(_ => Process(state, () =>
        {
            if (state.Help is null)
            {
                PromptHelp.Open(state, catalog);
            }
            else
            {
                PromptHelp.Close(state);
            }
        }), "Toggle instruction help");
        b.Key(Hex1bKey.Escape).Action(_ => Process(state, () => PromptHelp.Close(state)), "Return to editor");
        if (state.Help is { } help)
        {
            b.Key(Hex1bKey.PageUp).Action(_ => help.Scroll = Math.Max(0, help.Scroll - height), "Previous help page");
            b.Key(Hex1bKey.PageDown).Action(_ => help.Scroll += height, "Next help page");
            b.Key(Hex1bKey.UpArrow).Action(_ => help.Scroll = Math.Max(0, help.Scroll - 1), "Previous help line");
            b.Key(Hex1bKey.DownArrow).Action(_ => help.Scroll++, "Next help line");
            b.Key(Hex1bKey.Tab).Action(_ => Process(state,
                () => help.MoveAction(state, false, width, height)), "Next help target");
            b.Shift().Key(Hex1bKey.Tab).Action(_ => Process(state,
                () => help.MoveAction(state, true, width, height)), "Previous help target");
            b.Key(Hex1bKey.Enter).Action(_ => Process(state, () => help.Activate(state)), "Open help target");
            b.Key(Hex1bKey.F8).Action(_ => Process(state, () => PromptDiagnostics.Move(state, false)), "Next diagnostic");
            b.Shift().Key(Hex1bKey.F8).Action(_ => Process(state,
                () => PromptDiagnostics.Move(state, true)), "Previous diagnostic");
            b.Mouse(MouseButton.ScrollUp).Action(_ => help.Scroll = Math.Max(0, help.Scroll - 3), "Scroll help up");
            b.Mouse(MouseButton.ScrollDown).Action(_ => help.Scroll += 3, "Scroll help down");
        }

        b.Ctrl().Key(Hex1bKey.Q).Action(context => context.RequestStop(), "Quit");
        b.AnyCharacter().Action(_ => { }, "Read-only help");
    }

    /// <summary>
    /// Advances the browser acknowledgment only after a help action has finished.
    /// </summary>
    internal static void Process(PromptState state, Action action)
    {
        var previous = state.Help;
        action();
        state.HelpInputSequence++;
        if (!ReferenceEquals(previous, state.Help))
        {
            state.PasteInput?.PauseUntilFrame();
        }
    }
}
