using System.Globalization;
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
        var diagnostics = state.Analysis?.Diagnostics;
        return diagnostics?.FirstOrDefault(d => d.Location.Line == state.CaretLine - 1 && d.Kind == AnalysisDiagnosticKind.Error)
            ?? diagnostics?.FirstOrDefault(d => d.Location.Line == state.CaretLine - 1)
            ?? diagnostics?.FirstOrDefault(d => d.Kind == AnalysisDiagnosticKind.Error)
            ?? (diagnostics is { Count: > 0 } ? diagnostics[0] : null);
    }

    /// <summary>
    /// Formats the current explanation, retaining only its presentation while replacement analysis is pending.
    /// </summary>
    /// <param name="state">The prompt.</param>
    /// <returns>The explanation and its style, or null.</returns>
    public static DiagnosticDisplay? Display(PromptState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Current(state) is not { } diagnostic)
        {
            return state.Analysis is null ? state.PendingDiagnostic : null;
        }

        var kind = diagnostic.Kind switch
        {
            AnalysisDiagnosticKind.Error => "error",
            AnalysisDiagnosticKind.Unverifiable => "unverifiable",
            AnalysisDiagnosticKind.Unknown => "unknown",
            _ => "incomplete",
        };
        var count = state.Analysis!.Diagnostics.Count;
        var suffix = count > 1 ? $" ({count} findings; F8 next)" : "";
        var text = $"{kind} on line {diagnostic.Location.Line + 1}: {diagnostic.Message}{suffix}";
        return new DiagnosticDisplay(text, diagnostic.Kind == AnalysisDiagnosticKind.Error ? SpanStyle.Error : SpanStyle.Dim);
    }

    /// <summary>
    /// Wraps one explanation into at most three terminal rows.
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
        var lines = WrapWords(display.Text.ReplaceLineEndings(" "), columns).Take(4).ToArray();
        return lines.Length <= 3 ? lines : [lines[0], lines[1], PaletteText.Clip(lines[2], Math.Max(0, columns - 1)) + "…"];
    }

    private static IEnumerable<string> WrapWords(string text, int width)
    {
        while (text.Length > 0)
        {
            var portion = PaletteText.Clip(text, width, ellipsis: false);
            if (portion.Length == text.Length)
            {
                yield return portion;
                yield break;
            }

            if (portion.Length == 0)
            {
                yield return "…";
                text = text[StringInfo.GetNextTextElementLength(text)..];
                continue;
            }

            var boundary = portion.LastIndexOf(' ');
            var length = boundary > 0 ? boundary : portion.Length;
            yield return text[..length].TrimEnd();
            text = text[length..].TrimStart();
        }
    }

    /// <summary>
    /// Moves to the next or previous diagnostic without replacing or selecting source text.
    /// </summary>
    /// <param name="state">The prompt.</param>
    /// <param name="backwards">Whether to move to the previous location.</param>
    public static void Move(PromptState state, bool backwards)
    {
        ArgumentNullException.ThrowIfNull(state);
        var positions = state.Analysis?.Diagnostics.Select(d => d.Location).Where(location => location.Line >= 0)
            .Distinct().OrderBy(location => location.Line).ThenBy(location => location.Start).ToArray() ?? [];
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
}
