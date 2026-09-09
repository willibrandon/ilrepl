using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Classifies a REPL line without executing it, using the same rules for live input and previews.
/// </summary>
/// <remarks>
/// Reads a line the way <see cref="ReplCore"/> reads it, without doing anything: comment, blank,
/// command, one of the three meanings of <c>ret</c>, or a line for the session. The real handler
/// and a preview of the unsent buffer classify through this one place, so a preview never takes a
/// line for something the handler would not.
/// </remarks>
public static class ReplLineDispatcher
{
    /// <summary>
    /// Classifies a normalized line against the session's structural state.
    /// </summary>
    /// <param name="line">The line, its comments removed.</param>
    /// <param name="methodOpen">True while a <c>.method</c> block, top-level or member, is open.</param>
    /// <param name="hasPendingLabels">True when the cell references a label it has not defined.</param>
    /// <param name="blockOpen">True when the cell has an open protected region.</param>
    /// <returns>The operation.</returns>
    public static ReplLineOperation Classify(NormalizedLine line, bool methodOpen, bool hasPendingLabels, bool blockOpen)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Kind == SourceLineKind.Comment)
        {
            return new ReplLineOperation(ReplLineKind.Comment, line, null, "");
        }

        if (line.Kind == SourceLineKind.Blank)
        {
            return new ReplLineOperation(ReplLineKind.Blank, line, null, "");
        }

        var text = line.Text;
        if (text.StartsWith('.') && !IsDirective(text))
        {
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            var command = space < 0 ? text : text[..space];
            var argument = space < 0 ? "" : text[(space + 1)..].Trim();
            return new ReplLineOperation(ReplLineKind.Command, line, command, argument);
        }

        if (text == "ret" || text.StartsWith("ret ", StringComparison.Ordinal))
        {
            if (methodOpen)
            {
                return new ReplLineOperation(ReplLineKind.RetInMethod, line, null, "");
            }

            return new ReplLineOperation(hasPendingLabels || blockOpen ? ReplLineKind.RetInline : ReplLineKind.RetRuns, line, null, "");
        }

        return new ReplLineOperation(ReplLineKind.Line, line, null, "");
    }

    /// <summary>
    /// True when a dotted word is a directive the session takes rather than a command.
    /// </summary>
    /// <param name="text">The line text.</param>
    /// <returns>True for a directive.</returns>
    public static bool IsDirective(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var d in ReplDirectives.Names)
        {
            if (text.StartsWith(d, StringComparison.Ordinal) && (text.Length == d.Length || !char.IsLetter(text[d.Length])))
            {
                return true;
            }
        }

        return false;
    }
}
