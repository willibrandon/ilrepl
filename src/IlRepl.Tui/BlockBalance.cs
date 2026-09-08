using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Decides whether a buffer is complete: its closing braces, counted outside strings, quoted
/// names, and comments, reach its opening braces, no comment is left open, and no string is left
/// open on its last line. Enter submits a complete buffer and continues an incomplete one.
/// </summary>
public static class BlockBalance
{
    /// <summary>
    /// Scans a buffer's braces and lexical state, line by line.
    /// </summary>
    /// <param name="text">The buffer, lines separated by newlines.</param>
    /// <param name="openDepth">How many closing braces the engine is already waiting for.</param>
    /// <param name="inBlockComment">Whether the engine has a <c>/*</c> open when the buffer starts.</param>
    /// <returns>What the scan found.</returns>
    public static BlockScan Scan(string text, int openDepth = 0, bool inBlockComment = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var depth = openDepth;
        var comment = inBlockComment;
        var inString = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            inString = false;
            foreach (var segment in CilLexer.Segments(line, ref comment))
            {
                switch (segment.Kind)
                {
                    case CilSegmentKind.Code:
                        for (var i = segment.Start; i < segment.End; i++)
                        {
                            if (line[i] == '{')
                            {
                                depth++;
                            }
                            else if (line[i] == '}')
                            {
                                depth--;
                            }
                        }

                        break;
                    case CilSegmentKind.String:
                    case CilSegmentKind.QuotedName:
                        inString = IsUnterminated(line, segment);
                        break;
                    default:
                        break;
                }
            }
        }

        return new BlockScan(depth, comment, inString);
    }

    /// <summary>
    /// Whether Enter should submit the buffer rather than continue it.
    /// </summary>
    /// <param name="text">The buffer.</param>
    /// <param name="openDepth">How many closing braces the engine is already waiting for.</param>
    /// <param name="inBlockComment">Whether the engine has a <c>/*</c> open when the buffer starts.</param>
    /// <returns>True when the braces balance and nothing is left open.</returns>
    public static bool IsComplete(string text, int openDepth = 0, bool inBlockComment = false)
    {
        var scan = Scan(text, openDepth, inBlockComment);
        return scan.Depth <= 0 && !scan.InBlockComment && !scan.InString;
    }

    private static bool IsUnterminated(string line, CilSegment segment)
    {
        var quote = line[segment.Start];
        for (var i = segment.Start + 1; i < segment.End; i++)
        {
            if (line[i] == '\\')
            {
                i++;
            }
            else if (line[i] == quote)
            {
                return false;
            }
        }

        return true;
    }
}
