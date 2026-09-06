using Hex1b;
using Hex1b.Composition;
using Hex1b.Input;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// The input line of the REPL: a text box with a completion palette for opcodes and commands,
/// inline prediction of the best match, and Up/Down history.
/// </summary>
/// <param name="Label">The prompt label shown before the text box.</param>
/// <param name="Catalog">Every opcode and command the palette can offer.</param>
public sealed record PromptWidget(string Label, IReadOnlyList<CompletionItem> Catalog) : Hex1bWidget
{
    /// <summary>
    /// The most rows the palette shows at once.
    /// </summary>
    public const int PaletteRows = 8;

    internal Func<string, Task>? SubmitHandler { get; init; }

    /// <summary>
    /// Sets the handler awaited for each submitted line.
    /// </summary>
    /// <param name="handler">The handler.</param>
    /// <returns>A copy of the widget with the handler set.</returns>
    public PromptWidget OnSubmit(Func<string, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return this with { SubmitHandler = handler };
    }

    /// <summary>
    /// Builds the prompt row and, when there are candidates, the palette above it.
    /// </summary>
    /// <param name="ctx">The composition context.</param>
    /// <returns>The widget tree.</returns>
    protected override Hex1bWidget Build(CompositionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var state = ctx.UseState(() => new PromptState());
        var text = state.TextBox.Text;
        var word = FirstWord(text);
        var candidates = word.Length > 0 && word.Length == text.TrimStart().Length && !state.PaletteDismissed
            ? CatalogCompleter.Complete(Catalog, word)
            : [];
        if (candidates.Count == 1 && candidates[0].Name == word)
        {
            candidates = [];
        }

        var paletteVisible = candidates.Count > 0;
        state.SelectedIndex = paletteVisible ? Math.Clamp(state.SelectedIndex, 0, candidates.Count - 1) : 0;
        var catalog = Catalog;

        return ctx.VStack(v =>
        {
            var textBox = v.TextBox()
                .State(state.TextBox)
                .OnTextChanged(_ =>
                {
                    state.PaletteDismissed = false;
                    state.SelectedIndex = 0;
                })
                .OnSubmit(async e =>
                {
                    var line = e.Text ?? "";
                    if (line.Trim().Length > 0 && (state.History.Count == 0 || state.History[^1] != line))
                    {
                        state.History.Add(line);
                    }

                    state.HistoryIndex = state.History.Count;
                    state.Stash = null;
                    state.TextBox.Text = "";
                    state.TextBox.CursorPosition = 0;
                    if (SubmitHandler is not null)
                    {
                        await SubmitHandler(line).ConfigureAwait(true);
                    }
                })
                .Predict((current, _) =>
                {
                    var typed = FirstWord(current);
                    if (typed.Length == 0 || typed.Length != current.Length)
                    {
                        return Task.FromResult<string?>(null);
                    }

                    var matches = CatalogCompleter.Complete(catalog, typed);
                    var best = matches.Count > 0 ? matches[0] : null;
                    return Task.FromResult(best is null || best.Name == typed ? null : best.Name[typed.Length..]);
                })
                .InputBindings(b =>
                {
                    b.Key(Hex1bKey.Tab).Action(_ => Accept(state, candidates), "Accept completion");
                    b.Key(Hex1bKey.UpArrow).Action(_ =>
                    {
                        if (paletteVisible)
                        {
                            state.SelectedIndex = Math.Max(0, state.SelectedIndex - 1);
                        }
                        else
                        {
                            HistoryBack(state);
                        }
                    }, "Previous");
                    b.Key(Hex1bKey.DownArrow).Action(_ =>
                    {
                        if (paletteVisible)
                        {
                            state.SelectedIndex = Math.Min(candidates.Count - 1, state.SelectedIndex + 1);
                        }
                        else
                        {
                            HistoryForward(state);
                        }
                    }, "Next");
                    b.Key(Hex1bKey.Escape).Action(_ => state.PaletteDismissed = true, "Dismiss palette");
                });

            var row = v.HStack(h =>
            [
                h.ThemePanel(SpanPalette.Mutator(SpanStyle.Prompt), h.Text(Label).ContentWidth()).ContentWidth(),
                textBox.FillWidth(),
            ]);

            return paletteVisible ? [BuildPalette(v, candidates, state), row] : [row];
        });
    }

    private static string FirstWord(string text)
    {
        var trimmed = text.TrimStart();
        var space = trimmed.IndexOfAny([' ', '\t']);
        return space < 0 ? trimmed : trimmed[..space];
    }

    private static void Accept(PromptState state, IReadOnlyList<CompletionItem> candidates)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        var item = candidates[Math.Clamp(state.SelectedIndex, 0, candidates.Count - 1)];
        var completed = item.Name + (item.TakesOperand ? " " : "");
        state.TextBox.Text = completed;
        state.TextBox.CursorPosition = completed.Length;
        state.PaletteDismissed = true;
    }

    private static void HistoryBack(PromptState state)
    {
        if (state.HistoryIndex == 0)
        {
            return;
        }

        if (state.HistoryIndex == state.History.Count)
        {
            state.Stash = state.TextBox.Text;
        }

        state.HistoryIndex--;
        SetText(state, state.History[state.HistoryIndex]);
    }

    private static void HistoryForward(PromptState state)
    {
        if (state.HistoryIndex >= state.History.Count)
        {
            return;
        }

        state.HistoryIndex++;
        SetText(state, state.HistoryIndex == state.History.Count ? state.Stash ?? "" : state.History[state.HistoryIndex]);
    }

    private static void SetText(PromptState state, string text)
    {
        state.TextBox.Text = text;
        state.TextBox.CursorPosition = text.Length;
        state.PaletteDismissed = true;
    }

    private static BorderWidget BuildPalette(WidgetContext<VStackWidget> context, IReadOnlyList<CompletionItem> candidates, PromptState state)
    {
        var first = Math.Clamp(state.SelectedIndex - (PaletteRows / 2), 0, Math.Max(0, candidates.Count - PaletteRows));
        var visible = candidates.Skip(first).Take(PaletteRows).ToList();
        // Column widths come from every candidate, not just the visible ones, so the columns stay
        // put while the selection scrolls through the list.
        var nameWidth = Math.Max(12, candidates.Max(c => c.Name.Length) + 2);
        var detailWidth = Math.Max(4, candidates.Max(c => c.Detail.Length) + 2);

        var rows = visible.Select((item, offset) =>
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
                    }
                })
                .OnClick(_ => Accept(state, candidates));
        }).ToArray();

        var kind = candidates[0].Name.StartsWith('.') ? "commands" : "opcodes";
        var title = candidates.Count > PaletteRows ? $"{kind} {state.SelectedIndex + 1}/{candidates.Count}" : kind;
        return context.Border(b => rows).Title(title);
    }
}
