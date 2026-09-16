using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Builds contextual help from diagnostic facts using the transcript's familiar token and role styles.
/// </summary>
internal static class PromptHelpContent
{
    /// <summary>
    /// Presents diagnostic evidence without losing stack slots, incoming paths, or producer source.
    /// </summary>
    internal static IEnumerable<TranscriptLine> Diagnostic(AnalysisDiagnostic diagnostic, CilTokenizer tokenizer)
    {
        if (diagnostic.Explanation is not { } explanation)
        {
            return DiagnosticFormatter.Details(diagnostic).Select(text => TranscriptLine.Of(LineKind.Info, text));
        }

        var lines = new List<TranscriptLine>();
        if (explanation.Instruction is { } instruction)
        {
            lines.Add(new(LineKind.Info, tokenizer.Spans(instruction.Syntax)));
        }
        lines.Add(new(LineKind.Info, [new("Expected: ", SpanStyle.Dim), new(explanation.Requirement)]));
        if (explanation.Stack is { } stack)
        {
            lines.Add(new(LineKind.Info, [new("Stack before (bottom → top): ", SpanStyle.Dim), .. Stack(stack)]));
        }
        foreach (var conflict in explanation.Conflicts)
        {
            Value(conflict, "");
        }
        foreach (var path in explanation.Incoming)
        {
            lines.Add(new(LineKind.Info, [new("Incoming from ", SpanStyle.Dim), .. Source(path.Source, tokenizer),
                new(": ", SpanStyle.Dim), .. Stack(path.Stack)]));
            foreach (var value in path.Values)
            {
                Value(value, "  ");
            }
        }
        return lines;

        void Value(StackConflict value, string indent)
        {
            lines.Add(new(LineKind.Info, [new(indent + value.Role + ": expected ", SpanStyle.Dim), new(value.Expected),
                new("; actual ", SpanStyle.Dim), new(value.Actual ?? "missing")]));
            foreach (var producer in value.Producers.Distinct())
            {
                lines.Add(new(LineKind.Info, [new(indent + "  from ", SpanStyle.Dim), .. Source(producer, tokenizer)]));
            }
            if (value.Actual is not null && value.Producers.Count == 0)
            {
                lines.Add(TranscriptLine.Of(LineKind.Info, indent + "  producer unavailable", SpanStyle.Dim));
            }
        }
    }

    /// <summary>
    /// Formats a stack transition with compact spacing and highlights each side's top slot.
    /// </summary>
    internal static TranscriptLine Effect(string effect)
    {
        var spans = new List<TranscriptSpan> { new("Stack effect: ", SpanStyle.Dim) };
        var sides = effect.Split('→');
        for (var index = 0; index < sides.Length; index++)
        {
            if (index > 0)
            {
                spans.Add(new(" → ", SpanStyle.Punctuation));
            }
            var side = string.Join(" ", sides[index].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (side.StartsWith('[') && side.EndsWith(']'))
            {
                spans.AddRange(Slots(SplitSlots(side[1..^1])));
            }
            else
            {
                spans.Add(new(side, side.Any(char.IsDigit) && side.All(c => char.IsDigit(c) || c == ' ')
                    ? SpanStyle.Number : SpanStyle.TopType));
            }
        }
        return new(LineKind.Info, spans);
    }

    private static IEnumerable<TranscriptSpan> Source(AnalysisSource source, CilTokenizer tokenizer)
    {
        yield return new(DiagnosticFormatter.Source(source with { Source = "" }));
        if (source.Source.Length > 0)
        {
            yield return new(": ", SpanStyle.Dim);
            var snippet = source.Source.Trim();
            var spans = source.Kind == AnalysisSourceKind.Synthetic ? [new TranscriptSpan(snippet)] : tokenizer.Spans(snippet);
            foreach (var span in spans)
            {
                yield return span;
            }
        }
    }

    private static IEnumerable<TranscriptSpan> Stack(AnalyzedStack stack)
    {
        if (stack.Kind == AnalyzedStackKind.Known)
        {
            foreach (var span in Slots(stack.Values))
            {
                yield return span;
            }
        }
        else
        {
            yield return new(stack.Kind == AnalyzedStackKind.Unknown ? "unknown" : stack.Render());
            if (stack.Kind == AnalyzedStackKind.Unknown && stack.Values.Count > 0)
            {
                yield return new(" (established path: ", SpanStyle.Dim);
                foreach (var span in Slots(stack.Values))
                {
                    yield return span;
                }
                yield return new(")", SpanStyle.Dim);
            }
        }
        if (stack.Incomplete)
        {
            yield return new(" (incomplete body)", SpanStyle.Dim);
        }
    }

    private static IEnumerable<TranscriptSpan> Slots(IReadOnlyList<string> values)
    {
        yield return new("[", SpanStyle.Punctuation);
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                yield return new(", ", SpanStyle.Punctuation);
            }
            yield return new(values[index], index == values.Count - 1 ? SpanStyle.TopType : SpanStyle.Type);
        }
        yield return new("]", SpanStyle.Punctuation);
    }

    private static List<string> SplitSlots(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }
        var values = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '<' or '[' or '(':
                    depth++;
                    break;
                case '>' or ']' or ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    values.Add(text[start..index].Trim());
                    start = index + 1;
                    break;
            }
        }
        values.Add(text[start..].Trim());
        return values;
    }
}
