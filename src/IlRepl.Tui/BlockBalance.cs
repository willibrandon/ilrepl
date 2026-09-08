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
    /// <param name="commands">The dot-words that are commands, whose arguments hold no brace that counts; null to know none.</param>
    /// <returns>What the scan found.</returns>
    public static BlockScan Scan(string text, int openDepth = 0, bool inBlockComment = false, bool awaitingBrace = false, IReadOnlyCollection<string>? commands = null)
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
            var segments = CilLexer.Segments(line, ref comment);

            // A header takes its brace on the same line or the next: the block is open either
            // way, so the brace is counted now and the next one is its own. A handler header ends
            // the part before it and opens the next at the same depth, with or without a brace
            // of its own; without a leading brace the part before it is closed by the keyword
            // alone. A header is read from the line as the engine reads it, with its comments
            // taken out and its quoted text set aside. A command's argument is text, not code,
            // so a brace in it counts for nothing.
            var code = Code(line, segments);
            var command = IsCommand(code, commands);
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

            foreach (var segment in segments)
            {
                switch (segment.Kind)
                {
                    case CilSegmentKind.Code:
                        for (var i = segment.Start; !command && i < segment.End; i++)
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
    /// <param name="commands">The dot-words that are commands, whose arguments hold no brace that counts; null to know none.</param>
    /// <returns>True when the braces balance and nothing is left open.</returns>
    public static bool IsComplete(string text, int openDepth = 0, bool inBlockComment = false, bool awaitingBrace = false, IReadOnlyCollection<string>? commands = null)
    {
        var scan = Scan(text, openDepth, inBlockComment, awaitingBrace, commands);
        return scan.Depth <= 0 && !scan.InBlockComment && !scan.InString;
    }

    // The line's code as the engine sees it: comments taken out, so a word a comment parts is
    // one word, and a space where each string or quoted name was.
    private static string Code(string line, IReadOnlyList<CilSegment> segments)
    {
        var code = new System.Text.StringBuilder(line.Length);
        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case CilSegmentKind.Code:
                    code.Append(line, segment.Start, segment.Length);
                    break;
                case CilSegmentKind.String:
                case CilSegmentKind.QuotedName:
                    code.Append(' ');
                    break;
                default:
                    break;
            }
        }

        return code.ToString();
    }

    private static bool IsCommand(string code, IReadOnlyCollection<string>? commands)
    {
        if (commands is null)
        {
            return false;
        }

        var text = code.AsSpan().TrimStart();
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        var word = text[..end];
        if (word.Length < 2 || word[0] != '.')
        {
            return false;
        }

        foreach (var command in commands)
        {
            if (word.Equals(command, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
