using Hex1b;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

public sealed partial record PromptWidget
{
    /// <summary>
    /// Provides layout rows, including disabled previous rows while an edited operand is being confirmed.
    /// </summary>
    /// <param name="state">The prompt.</param>
    /// <param name="catalog">The first-word catalog.</param>
    /// <returns>Current rows or dimmed display-only rows.</returns>
    public static IReadOnlyList<CompletionItem> DisplayCandidates(PromptState state, IReadOnlyList<CompletionItem> catalog)
    {
        var candidates = Candidates(state, catalog);
        return candidates.Count != 0 ? candidates
            : state.Palette == PaletteMode.Requested && !state.Busy && (state.Requester is { IsPending: true } || state.MoreCompletions)
                && state.PendingDisplay is { } display ? display.Visible() : [];
    }

    /// <summary>
    /// Wraps the selected operand's complete detail without shortening its signature.
    /// </summary>
    /// <param name="state">The prompt.</param>
    /// <param name="catalog">The first-word catalog.</param>
    /// <param name="width">The terminal width.</param>
    /// <returns>Every detail line, or an empty list when no operand is selected.</returns>
    public static IReadOnlyList<string> DetailLines(PromptState state, IReadOnlyList<CompletionItem> catalog, int width)
    {
        var candidates = DisplayCandidates(state, catalog);
        return candidates.Count > 0
            && candidates[Math.Clamp(state.SelectedIndex, 0, candidates.Count - 1)].FullDetail is { Length: > 0 } detail
                ? PaletteText.Wrap(detail, Math.Max(1, (width <= 0 ? 80 : width) - 2)) : [];
    }

    private static BorderWidget BuildPalette(
        WidgetContext<VStackWidget> context, IReadOnlyList<CompletionItem> candidates, PromptState state, PromptFit fit, int width)
    {
        var rows = Math.Max(1, fit.PaletteRows);
        var first = Math.Clamp(state.SelectedIndex - (rows / 2), 0, Math.Max(0, candidates.Count - rows));
        var innerWidth = Math.Max(1, width - 2);
        var nameWidth = Math.Min(candidates.Max(item => DisplayWidth.GetStringWidth(item.Name)) + 2,
            Math.Max(1, (int)(innerWidth * 0.45)));
        var detailWidth = Math.Min(candidates.Max(item => DisplayWidth.GetStringWidth(item.Detail)) + 2,
            Math.Max(1, innerWidth / 4));
        var version = state.Editor.Document.Version;
        var caret = state.Editor.Cursor.Position;
        var snapshot = state.Completions;
        var updating = state.PendingDisplay is not null;
        var lines = new List<Hex1bWidget>();
        foreach (var (item, offset) in candidates.Skip(first).Take(rows).Select((item, index) => (item, index)))
        {
            var index = first + offset;
            var selected = !updating && index == state.SelectedIndex;
            var line = (selected ? " ❯ " : "   ") + PaletteText.Column(item.Name, nameWidth)
                + PaletteText.Column(item.Detail, detailWidth) + item.Description;
            var style = updating ? SpanStyle.Dim : selected ? SpanStyle.TopType : ItemStyle(item);
            if (updating)
            {
                lines.Add(context.ThemePanel(SpanPalette.Mutator(style), context.Text(PaletteText.Clip(line, innerWidth))));
                continue;
            }

            lines.Add(context.Interactable(ic => ic.ThemePanel(SpanPalette.Mutator(style),
                    ic.Text(PaletteText.Clip(line, innerWidth))))
                .OnHoverChanged(args =>
                {
                    if (args.IsHovered && Current())
                    {
                        state.SelectedIndex = index;
                        state.PaletteNavigated = true;
                        state.DetailScroll = 0;
                    }
                })
                .OnClick(args =>
                {
                    if (Current())
                    {
                        state.SelectedIndex = index;
                        Accept(state, candidates);
                        args.Context.FocusWhere(node => node is EditorNode);
                    }
                }));
        }

        if (fit.DetailRows > 1 && candidates[state.SelectedIndex].FullDetail is { } detail)
        {
            var wrapped = PaletteText.Wrap(detail, innerWidth);
            var shown = fit.DetailRows - 1;
            state.DetailScroll = Math.Clamp(state.DetailScroll, 0, Math.Max(0, wrapped.Count - shown));
            var title = wrapped.Count > shown ? $"detail {state.DetailScroll + 1}/{wrapped.Count - shown + 1}  PgUp/PgDn" : "detail";
            lines.Add(context.ThemePanel(SpanPalette.Mutator(SpanStyle.Dim), context.Text(PaletteText.Clip(title, innerWidth))));
            foreach (var line in wrapped.Skip(state.DetailScroll).Take(shown))
            {
                lines.Add(updating ? context.ThemePanel(SpanPalette.Mutator(SpanStyle.Dim), context.Text(line)) : context.Text(line));
            }
        }

        return context.Border(_ => lines.ToArray()).Title(PaletteText.Clip(PaletteTitle(candidates, state, rows), innerWidth));

        bool Current() => state.Editor.Document.Version == version && state.Editor.Cursor.Position == caret
            && !state.PaletteDismissed && (candidates[0].Kind == CompletionKind.None
                || ReferenceEquals(snapshot, state.Completions) && snapshot is not null
                    && state.Requester?.Matches(state, snapshot) == true);
    }

    private static SpanStyle ItemStyle(CompletionItem item) => item.Kind switch
    {
        CompletionKind.Types or CompletionKind.GenericParameters or CompletionKind.TypeArguments => SpanStyle.Type,
        CompletionKind.Locals or CompletionKind.Arguments => SpanStyle.Input,
        CompletionKind.Labels => SpanStyle.Label,
        CompletionKind.None => SpanStyle.Opcode,
        _ => SpanStyle.Member,
    };

    private static string PaletteTitle(IReadOnlyList<CompletionItem> candidates, PromptState state, int rows)
    {
        var reply = candidates[0].Kind == CompletionKind.None ? null : (state.PendingDisplay ?? state.Completions)?.Reply;
        var kind = reply?.Kind switch
        {
            CompletionKind.Types => "types",
            CompletionKind.Members => "members",
            CompletionKind.Fields => "fields",
            CompletionKind.Methods => "methods",
            CompletionKind.Locals => "locals",
            CompletionKind.Arguments => "arguments",
            CompletionKind.Labels => "labels",
            CompletionKind.GenericParameters => "generic parameters",
            CompletionKind.TypeArguments => "type argument " + string.Join(", ", reply.Owners),
            CompletionKind.Signatures => "signatures",
            _ => candidates[0].Name.StartsWith('.') ? "commands" : "opcodes",
        };
        var total = reply?.Total ?? candidates.Count;
        var provisional = reply?.TotalIsProvisional == true ? "~" : "";
        return state.PendingDisplay is not null ? "updating " + kind
            : total > rows || reply is not null ? $"{kind} {state.SelectedIndex + 1}/{provisional}{total}" : kind;
    }
}
