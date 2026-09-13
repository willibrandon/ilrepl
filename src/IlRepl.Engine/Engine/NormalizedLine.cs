using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// A line of input with its comments removed, exactly once, before anything looks at it. The
/// text is what the session parses and stores; the raw line is what the transcript echoes.
/// </summary>
/// <param name="Raw">The line as typed.</param>
/// <param name="Text">The line without comments, trimmed.</param>
/// <param name="Kind">What the line amounts to.</param>
/// <param name="InBlockCommentBefore">Whether a <c>/*</c> comment was open when the line began.</param>
public sealed record NormalizedLine(string Raw, string Text, SourceLineKind Kind, bool InBlockCommentBefore)
{
    /// <summary>
    /// The original editor location when the line came from a submitted document.
    /// </summary>
    public AnalysisLocation? Location { get; init; }

    /// <summary>
    /// Wraps text that holds no comments, such as a stored line being replayed.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The line.</returns>
    public static NormalizedLine FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        return new NormalizedLine(text, trimmed, trimmed.Length == 0 ? SourceLineKind.Blank : SourceLineKind.Text, false);
    }
}
