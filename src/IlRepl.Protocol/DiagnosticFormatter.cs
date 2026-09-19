namespace IlRepl.Protocol;

/// <summary>
/// Formats diagnostic evidence consistently in the editor, transcript, and batch output.
/// </summary>
public static class DiagnosticFormatter
{
    /// <summary>
    /// Formats the evidence following a diagnostic's primary message.
    /// </summary>
    public static IEnumerable<string> Details(AnalysisDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (diagnostic.Explanation is not { } explanation)
        {
            foreach (var related in diagnostic.Related.Distinct())
            {
                yield return Location(related.Location) + ": " + related.Message;
            }

            yield break;
        }

        if (explanation.Instruction is { } instruction)
        {
            yield return instruction.Syntax;
        }

        yield return "Expected: " + explanation.Requirement;
        if (explanation.Stack is { } stack)
        {
            yield return "Stack before (bottom → top): " + Stack(stack);
        }

        foreach (var conflict in explanation.Conflicts)
        {
            foreach (var line in Value(conflict))
            {
                yield return line;
            }
        }

        foreach (var path in explanation.Incoming)
        {
            yield return "Incoming from " + Source(path.Source) + ": " + Stack(path.Stack);
            foreach (var value in path.Values)
            {
                foreach (var line in Value(value))
                {
                    yield return "  " + line;
                }
            }
        }
    }

    /// <summary>
    /// Describes a source without treating accepted or imported locations as editor lines.
    /// </summary>
    public static string Source(AnalysisSource source)
    {
        var location = source.Kind switch
        {
            AnalysisSourceKind.Document => Location(source.Location),
            AnalysisSourceKind.Imported => source.Location.Body + " " + Location(source.Location),
            AnalysisSourceKind.Synthetic => "implicit entry",
            AnalysisSourceKind.Unavailable => "source location unavailable",
            _ => source.Location.Line >= 0 ? "accepted " + Location(source.Location) : "previously accepted source",
        };

        return location + (source.Source.Length == 0 ? "" : ": " + source.Source.Trim());
    }

    /// <summary>
    /// Formats a source line or IL offset, explicitly identifying a missing location.
    /// </summary>
    public static string Location(AnalysisLocation location) => location.Offset is { } offset ? $"IL_{offset:x4}"
        : location.Line >= 0 ? $"line {location.Line + 1}" : "source location unavailable";

    private static string Stack(AnalyzedStack stack) => (stack.Kind == AnalyzedStackKind.Unknown ? "unknown" : stack.Render())
        + (stack.Kind == AnalyzedStackKind.Unknown && stack.Values.Count > 0
            ? " (established path: [" + string.Join(", ", stack.Values) + "])" : "")
        + (stack.Incomplete ? " (incomplete body)" : "");

    private static IEnumerable<string> Value(StackConflict value)
    {
        yield return value.Role + ": expected " + value.Expected + "; actual " + (value.Actual ?? "missing");
        foreach (var producer in value.Producers.Distinct())
        {
            yield return "  from " + Source(producer);
        }

        if (value.Actual is not null && value.Producers.Count == 0)
        {
            yield return "  producer unavailable";
        }
    }
}
