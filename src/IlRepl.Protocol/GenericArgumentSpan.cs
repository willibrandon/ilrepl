namespace IlRepl.Protocol;

/// <summary>
/// Locates the generic argument list containing the caret using the same quoted and commented lexemes as classification.
/// </summary>
/// <param name="Open">The offset of the opening angle bracket.</param>
/// <param name="Close">The offset of the closing angle bracket, or -1 for an unfinished list.</param>
/// <param name="Arguments">The argument components, including an unfinished final component.</param>
public sealed record GenericArgumentSpan(int Open, int Close, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// Finds the innermost list at the caret or the just-closed list before a method signature site.
    /// </summary>
    /// <param name="line">The complete caret line.</param>
    /// <param name="caret">The UTF-16 caret offset.</param>
    /// <param name="inBlockComment">The lexical state before this line.</param>
    /// <param name="afterClose">Whether the caret requests the signature following a closed method argument list.</param>
    /// <returns>The list, or null when the caret is outside one.</returns>
    public static GenericArgumentSpan? At(string line, int caret, bool inBlockComment, bool afterClose = false)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentOutOfRangeException.ThrowIfNegative(caret);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(caret, line.Length);
        var lexemes = CilScanner.Scan(line, ref inBlockComment);
        var opens = new Stack<int>();
        var pairs = new Dictionary<int, int>();
        var selected = -1;
        var closed = -1;
        foreach (var lexeme in lexemes)
        {
            if (lexeme.Kind != CilLexemeKind.Punctuation)
            {
                continue;
            }

            if (line[lexeme.Start] == '<')
            {
                opens.Push(lexeme.Start);
                if (lexeme.Start < caret)
                {
                    selected = lexeme.Start;
                }
            }
            else if (line[lexeme.Start] == '>' && opens.TryPop(out var open))
            {
                pairs[open] = lexeme.Start;
                if (lexeme.End <= caret)
                {
                    selected = opens.TryPeek(out var outer) ? outer : -1;
                    closed = line.AsSpan(lexeme.End, caret - lexeme.End).Trim().Length == 0 ? open : -1;
                }
            }
        }

        if (afterClose)
        {
            selected = closed;
        }

        if (selected < 0)
        {
            return null;
        }

        var end = pairs.GetValueOrDefault(selected, -1);
        var limit = end < 0 ? line.Length : end;
        var argumentStart = selected + 1;
        var arguments = new List<string>();
        var depth = 0;
        foreach (var lexeme in lexemes.Where(lexeme => lexeme.Start > selected && lexeme.Start < limit))
        {
            if (lexeme.Kind != CilLexemeKind.Punctuation)
            {
                continue;
            }

            var character = line[lexeme.Start];
            if (character is '<' or '(' or '[')
            {
                depth++;
            }
            else if (character is '>' or ')' or ']')
            {
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                arguments.Add(line[argumentStart..lexeme.Start].Trim());
                argumentStart = lexeme.End;
            }
        }

        arguments.Add(line[argumentStart..limit].Trim());
        return new GenericArgumentSpan(selected, end, arguments);
    }
}
