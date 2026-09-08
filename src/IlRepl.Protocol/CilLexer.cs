using System.Text;

namespace IlRepl.Protocol;

/// <summary>
/// The one reader of strings, quoted names, and comments in a line of IL. The engine strips
/// comments with it before a line is routed anywhere, the editor decides whether a buffer's
/// braces are balanced with it, and the tokenizer colours with it, so all three agree on where a
/// string ends and where a comment begins. A <c>/* */</c> comment may span lines; the state
/// between lines is one flag.
/// </summary>
public static class CilLexer
{
    /// <summary>
    /// Splits a line into code, strings, quoted names, and comments, in order and without gaps.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="inBlockComment">Whether a <c>/*</c> from an earlier line is still open; updated for the next line.</param>
    /// <returns>The segments, covering the whole line.</returns>
    public static IReadOnlyList<CilSegment> Segments(string line, ref bool inBlockComment)
    {
        ArgumentNullException.ThrowIfNull(line);
        var segments = new List<CilSegment>();
        var i = 0;
        if (inBlockComment)
        {
            var close = line.IndexOf("*/", StringComparison.Ordinal);
            if (close < 0)
            {
                if (line.Length > 0)
                {
                    segments.Add(new CilSegment(0, line.Length, CilSegmentKind.BlockComment));
                }

                return segments;
            }

            i = close + 2;
            segments.Add(new CilSegment(0, i, CilSegmentKind.BlockComment));
            inBlockComment = false;
        }

        var codeStart = i;
        while (i < line.Length)
        {
            var c = line[i];
            if (c == '"')
            {
                Flush(segments, codeStart, i);
                var end = EndOfString(line, i);
                segments.Add(new CilSegment(i, end - i, CilSegmentKind.String));
                i = end;
                codeStart = i;
            }
            else if (c == '\'')
            {
                Flush(segments, codeStart, i);
                var end = EndOfQuotedName(line, i);
                segments.Add(new CilSegment(i, end - i, CilSegmentKind.QuotedName));
                i = end;
                codeStart = i;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                Flush(segments, codeStart, i);
                segments.Add(new CilSegment(i, line.Length - i, CilSegmentKind.LineComment));
                i = line.Length;
                codeStart = i;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                Flush(segments, codeStart, i);
                var close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    segments.Add(new CilSegment(i, line.Length - i, CilSegmentKind.BlockComment));
                    inBlockComment = true;
                    i = line.Length;
                }
                else
                {
                    segments.Add(new CilSegment(i, close + 2 - i, CilSegmentKind.BlockComment));
                    i = close + 2;
                }

                codeStart = i;
            }
            else
            {
                i++;
            }
        }

        Flush(segments, codeStart, line.Length);
        return segments;
    }

    /// <summary>
    /// Removes the comments from a line and keeps everything else where it was, strings and quoted
    /// names untouched. A <c>/*</c> with no <c>*/</c> comments out the rest of the line and every
    /// line after it until one closes it.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="inBlockComment">Whether a <c>/*</c> from an earlier line is still open; updated for the next line.</param>
    /// <returns>The line without its comments.</returns>
    public static string StripComments(string line, ref bool inBlockComment)
    {
        ArgumentNullException.ThrowIfNull(line);
        var segments = Segments(line, ref inBlockComment);
        if (segments.Count == 1 && segments[0].Kind == CilSegmentKind.Code)
        {
            return line;
        }

        var result = new StringBuilder(line.Length);
        foreach (var segment in segments)
        {
            if (segment.Kind is not (CilSegmentKind.LineComment or CilSegmentKind.BlockComment))
            {
                result.Append(line, segment.Start, segment.Length);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Finds where a double-quoted string ends: the offset just past its closing quote, or the end
    /// of the line when it never closes. A backslash escapes the character after it.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="open">The offset of the opening quote.</param>
    /// <returns>The offset just past the string.</returns>
    public static int EndOfString(string line, int open) => EndOfQuoted(line, open, '"');

    /// <summary>
    /// Finds where a single-quoted name ends: the offset just past its closing quote, or the end
    /// of the line when it never closes. A backslash escapes the character after it.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="open">The offset of the opening quote.</param>
    /// <returns>The offset just past the name.</returns>
    public static int EndOfQuotedName(string line, int open) => EndOfQuoted(line, open, '\'');

    private static int EndOfQuoted(string line, int open, char quote)
    {
        ArgumentNullException.ThrowIfNull(line);
        for (var i = open + 1; i < line.Length; i++)
        {
            if (line[i] == '\\')
            {
                i++;
            }
            else if (line[i] == quote)
            {
                return i + 1;
            }
        }

        return line.Length;
    }

    private static void Flush(List<CilSegment> segments, int start, int end)
    {
        if (end > start)
        {
            segments.Add(new CilSegment(start, end - start, CilSegmentKind.Code));
        }
    }
}
