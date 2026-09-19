using Hex1b.Documents;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Keeps diagnostic explanations compact and moves the caret between their source locations.
/// </summary>
public static class PromptDiagnostics
{
    /// <summary>
    /// Selects the caret's diagnostic, followed by the first correctness error.
    /// </summary>
    /// <param name="state">The current prompt.</param>
    /// <returns>The finding to explain, or null.</returns>
    public static AnalysisDiagnostic? Current(PromptState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Current(state, Visible(state));
    }

    private static AnalysisDiagnostic? Current(PromptState state, IReadOnlyList<AnalysisDiagnostic> diagnostics) =>
        diagnostics.FirstOrDefault(d => AtCaret(d, state) && d.Kind == AnalysisDiagnosticKind.Error)
        ?? diagnostics.FirstOrDefault(d => AtCaret(d, state))
        ?? diagnostics.FirstOrDefault(d => d.Kind == AnalysisDiagnosticKind.Error)
        ?? (diagnostics.Count > 0 ? diagnostics[0] : null);

    /// <summary>
    /// Omits a refused current operand while its completion request has confirmed choices.
    /// </summary>
    internal static IReadOnlyList<AnalysisDiagnostic> Visible(PromptState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var diagnostics = state.Analyzer is { } analyzer ? analyzer.Diagnostics(state) : state.Analysis?.Diagnostics ?? [];
        return Visible(state, diagnostics);
    }

    private static IReadOnlyList<AnalysisDiagnostic> Visible(PromptState state, IReadOnlyList<AnalysisDiagnostic> diagnostics)
    {
        if (!CompletionOwnsCaret(state))
        {
            return diagnostics;
        }

        var line = state.CaretLine - 1;
        return diagnostics.Where(diagnostic => diagnostic.Code != "FLOW008" || diagnostic.Location.Line != line).ToArray();
    }

    /// <summary>
    /// Formats the current explanation, retaining only its presentation while replacement analysis is pending.
    /// </summary>
    /// <param name="state">The prompt.</param>
    /// <returns>The explanation and its style, or null.</returns>
    public static DiagnosticDisplay? Display(PromptState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Display(state, Visible(state));
    }

    /// <summary>
    /// Retains only the previous explanation's presentation while fresh source evidence is unavailable.
    /// </summary>
    internal static DiagnosticDisplay? PreviousDisplay(PromptState state) => Display(state, state.Analysis?.Diagnostics ?? []);

    private static DiagnosticDisplay? Display(PromptState state, IReadOnlyList<AnalysisDiagnostic> diagnostics)
    {
        if (Current(state, diagnostics) is not { } diagnostic)
        {
            return state.Analysis is null ? state.PendingDiagnostic : null;
        }

        return Display(diagnostic, diagnostics.Count);
    }

    /// <summary>
    /// Formats a selected diagnostic without rechecking the identity of its analysis.
    /// </summary>
    internal static DiagnosticDisplay Display(AnalysisDiagnostic diagnostic, int count)
    {
        var kind = diagnostic.Kind switch
        {
            AnalysisDiagnosticKind.Error => "error",
            AnalysisDiagnosticKind.Unverifiable => "unverifiable",
            AnalysisDiagnosticKind.Unknown => "unknown",
            _ => "incomplete",
        };

        var suffix = count > 1 ? $" ({count} findings; F8 next)" : "";
        var where = diagnostic.Explanation?.Source is { Kind: not AnalysisSourceKind.Document } source
            ? " at " + DiagnosticFormatter.Source(source) : $" on line {diagnostic.Location.Line + 1}";
        var text = $"{kind}{where}: {diagnostic.Message}{suffix}";
        return new DiagnosticDisplay(text, diagnostic.Kind == AnalysisDiagnosticKind.Error ? SpanStyle.Error : SpanStyle.Dim);
    }

    /// <summary>
    /// Uses transcript word wrapping to fit one explanation into at most three terminal rows.
    /// </summary>
    /// <param name="state">The prompt.</param>
    /// <param name="width">The available terminal columns.</param>
    /// <returns>The visible rows.</returns>
    public static IReadOnlyList<string> Lines(PromptState state, int width)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Display(state) is not { } display)
        {
            return [];
        }

        var columns = Math.Max(1, width <= 0 ? 80 : width);
        var lines = PaletteText.WrapWords(display.Text.ReplaceLineEndings(" "), columns).Take(4).ToArray();
        return lines.Length <= 3 ? lines : [lines[0], lines[1], PaletteText.Clip(lines[2], Math.Max(0, columns - 1)) + "…"];
    }

    /// <summary>
    /// Moves to the next or previous diagnostic without replacing or selecting source text.
    /// </summary>
    /// <param name="state">The prompt.</param>
    /// <param name="backwards">Whether to move to the previous location.</param>
    public static void Move(PromptState state, bool backwards)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Analyzer?.Refresh(state);
        var diagnostics = state.Analyzer is { } analyzer ? Visible(state, analyzer.NavigationDiagnostics(state)) : Visible(state);
        var positions = diagnostics.Where(d => d.Explanation?.Source is null or { Kind: AnalysisSourceKind.Document })
            .Select(d => d.Location).Where(location => location.Line >= 0 && location.Line < state.LineCount && location.Offset is null)
            .Distinct().OrderBy(location => location.Line).ThenBy(location => location.Start).ToArray();
        if (positions.Length == 0)
        {
            return;
        }

        var line = state.CaretLine - 1;
        var column = state.CaretColumn;
        var location = backwards
            ? positions.LastOrDefault(p => p.Line < line || p.Line == line && p.Start < column) ?? positions[^1]
            : positions.FirstOrDefault(p => p.Line > line || p.Line == line && p.Start > column) ?? positions[0];
        var document = state.Editor.Document;
        var targetLine = Math.Clamp(location.Line + 1, 1, document.LineCount);
        var targetColumn = Math.Clamp(location.Start, 0, document.GetLineText(targetLine).Length) + 1;
        state.Editor.SetCursorPosition(document.PositionToOffset(new DocumentPosition(targetLine, targetColumn)));
    }

    private static bool CompletionOwnsCaret(PromptState state)
    {
        if (state.Palette == PaletteMode.Requested)
        {
            return state.Requester?.IsPending == true || state.MoreCompletions || state.PendingDisplay is not null;
        }

        return state.Palette == PaletteMode.Open && state.Completions is { Reply.Items.Count: > 0 } completion
            && state.Requester?.Matches(state, completion) == true;
    }

    /// <summary>
    /// Matches only diagnostics belonging to the caret's current document source.
    /// </summary>
    internal static bool AtCaret(AnalysisDiagnostic diagnostic, PromptState state) =>
        diagnostic.Location.Line == state.CaretLine - 1
        && diagnostic.Explanation?.Source is null or { Kind: AnalysisSourceKind.Document };
}
