using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// The indentation the editor adds and takes away: a new line copies the whitespace of the text
/// before the caret and steps in once after an opening brace, and a closing brace typed on a
/// blank line steps out once, which is how <c>.show</c> indents a listing.
/// </summary>
public static class AutoIndent
{
    /// <summary>
    /// One step of indentation.
    /// </summary>
    public const string Unit = "  ";

    /// <summary>
    /// The indentation for the line Enter opens under the text before the caret: that text's
    /// leading whitespace, tabs kept, plus one unit when it ends with an opening brace outside a
    /// string or comment.
    /// </summary>
    /// <param name="textBeforeCaret">The current line up to the caret.</param>
    /// <param name="inBlockComment">Whether a <c>/*</c> was open when the line began.</param>
    /// <returns>The whitespace to start the new line with.</returns>
    public static string Continuation(string textBeforeCaret, bool inBlockComment = false)
    {
        ArgumentNullException.ThrowIfNull(textBeforeCaret);
        var leading = LeadingWhitespace(textBeforeCaret);
        var code = CilLexer.StripComments(textBeforeCaret, ref inBlockComment).TrimEnd();
        return code.EndsWith('{') ? leading + Unit : leading;
    }

    /// <summary>
    /// Whether a closing brace typed at the caret should step the line out: the line so far is
    /// whitespace of at least one unit and the caret is outside any comment or string.
    /// </summary>
    /// <param name="textBeforeCaret">The current line up to the caret.</param>
    /// <param name="inBlockComment">Whether a <c>/*</c> was open when the line began.</param>
    /// <returns>True when the brace should be dedented.</returns>
    public static bool ShouldDedent(string textBeforeCaret, bool inBlockComment = false)
    {
        ArgumentNullException.ThrowIfNull(textBeforeCaret);
        return !inBlockComment && textBeforeCaret.Trim().Length == 0 && (textBeforeCaret.Length >= Unit.Length || textBeforeCaret.Contains('\t', StringComparison.Ordinal));
    }

    /// <summary>
    /// Takes one unit off the leading whitespace of a line when it has at least one.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The line stepped out once, or unchanged.</returns>
    public static string Dedent(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var leading = LeadingWhitespace(line);
        if (leading.EndsWith('\t'))
        {
            return line[..(leading.Length - 1)] + line[leading.Length..];
        }

        return leading.Length >= Unit.Length ? line[..(leading.Length - Unit.Length)] + line[leading.Length..] : line;
    }

    /// <summary>
    /// The leading whitespace of a line.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The whitespace, tabs kept.</returns>
    public static string LeadingWhitespace(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }

        return line[..i];
    }
}
