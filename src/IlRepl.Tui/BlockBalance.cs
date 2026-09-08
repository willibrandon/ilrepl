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
    /// <param name="awaitingBrace">True when the engine or the lines before hold a declaration header whose brace has not come yet.</param>
    /// <returns>What the scan found.</returns>
    public static BlockScan Scan(string text, int openDepth = 0, bool inBlockComment = false, bool awaitingBrace = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var depth = openDepth;
        var comment = inBlockComment;
        var inString = false;
        var awaiting = awaitingBrace;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            inString = false;
            var first = true;
            foreach (var segment in CilLexer.Segments(line, ref comment))
            {
                switch (segment.Kind)
                {
                    case CilSegmentKind.Code:
                        var code = line.AsSpan(segment.Start, segment.End - segment.Start);
                        if (first && !code.IsWhiteSpace())
                        {
                            // A header takes its brace on the same line or the next: the block is
                            // open either way, so the brace is counted now and the next one is its
                            // own. A handler header ends the part before it and opens the next at
                            // the same depth, with or without a brace of its own; without a
                            // leading brace the part before it is closed by the keyword alone.
                            first = false;
                            if (OpensADeclaration(code))
                            {
                                awaiting = true;
                                depth++;
                            }
                            else if (OpensAHandler(code, out var afterBrace))
                            {
                                awaiting = true;
                                if (!afterBrace)
                                {
                                    depth--;
                                }

                                depth++;
                            }
                        }

                        for (var i = segment.Start; i < segment.End; i++)
                        {
                            if (line[i] == '{')
                            {
                                if (awaiting)
                                {
                                    awaiting = false;
                                }
                                else
                                {
                                    depth++;
                                }
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

        return new BlockScan(depth, comment, inString, awaiting);
    }

    /// <summary>
    /// Whether Enter should submit the buffer rather than continue it.
    /// </summary>
    /// <param name="text">The buffer.</param>
    /// <param name="openDepth">How many closing braces the engine is already waiting for.</param>
    /// <param name="inBlockComment">Whether the engine has a <c>/*</c> open when the buffer starts.</param>
    /// <param name="awaitingBrace">True when a declaration header before the text still waits for its brace.</param>
    /// <returns>True when the braces balance and nothing is left open.</returns>
    public static bool IsComplete(string text, int openDepth = 0, bool inBlockComment = false, bool awaitingBrace = false)
    {
        var scan = Scan(text, openDepth, inBlockComment, awaitingBrace);
        return scan.Depth <= 0 && !scan.InBlockComment && !scan.InString;
    }

    private static bool OpensADeclaration(ReadOnlySpan<char> code)
    {
        var text = code.TrimStart();
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        var word = text[..end];
        return word.SequenceEqual(".method") || word.SequenceEqual(".class") || word.SequenceEqual(".property") || word.SequenceEqual(".event") || word.SequenceEqual(".try");
    }

    // catch, filter, finally, fault, or handler, either on its own or after the brace that closes
    // the part before it.
    private static bool OpensAHandler(ReadOnlySpan<char> code, out bool afterBrace)
    {
        var text = code.TrimStart();
        afterBrace = text.Length > 0 && text[0] == '}';
        if (afterBrace)
        {
            text = text[1..].TrimStart();
        }

        var end = 0;
        while (end < text.Length && char.IsLetter(text[end]))
        {
            end++;
        }

        var word = text[..end];
        var handler = word.SequenceEqual("catch") || word.SequenceEqual("filter") || word.SequenceEqual("finally") || word.SequenceEqual("fault") || word.SequenceEqual("handler");
        return handler && (end == text.Length || !char.IsLetterOrDigit(text[end]));
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
