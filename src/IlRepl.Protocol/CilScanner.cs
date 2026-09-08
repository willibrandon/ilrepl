namespace IlRepl.Protocol;

/// <summary>
/// Cuts a line into lexemes: words, numbers, strings, quoted names, comments, and punctuation.
/// It knows nothing about roles; the tokenizer decides those. Comments and quoting come from
/// <see cref="CilLexer"/>, so the cut agrees with the engine's.
/// </summary>
internal static class CilScanner
{
    /// <summary>
    /// Scans a line.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="inBlockComment">Whether a <c>/*</c> from an earlier line is still open; updated for the next line.</param>
    /// <returns>The lexemes, in order, whitespace left out.</returns>
    public static List<CilLexeme> Scan(string line, ref bool inBlockComment)
    {
        var lexemes = new List<CilLexeme>();
        var byteList = false;
        var byteListNext = false;
        foreach (var segment in CilLexer.Segments(line, ref inBlockComment))
        {
            switch (segment.Kind)
            {
                case CilSegmentKind.String:
                    lexemes.Add(new CilLexeme(segment.Start, segment.Length, CilLexemeKind.String));
                    byteListNext = false;
                    break;
                case CilSegmentKind.QuotedName:
                    lexemes.Add(new CilLexeme(segment.Start, segment.Length, CilLexemeKind.Quoted));
                    byteListNext = false;
                    break;
                case CilSegmentKind.LineComment:
                case CilSegmentKind.BlockComment:
                    lexemes.Add(new CilLexeme(segment.Start, segment.Length, CilLexemeKind.Comment));
                    break;
                default:
                    ScanCode(line, segment.Start, segment.End, lexemes, ref byteList, ref byteListNext);
                    break;
            }
        }

        // A quoted segment that touches a word is part of that name: N.'<>c'/Inner is one type.
        for (var i = lexemes.Count - 2; i >= 0; i--)
        {
            var left = lexemes[i];
            var right = lexemes[i + 1];
            if (left.End == right.Start && left.Kind is CilLexemeKind.Word or CilLexemeKind.Quoted && right.Kind is CilLexemeKind.Word or CilLexemeKind.Quoted && (left.Kind == CilLexemeKind.Word || right.Kind == CilLexemeKind.Word))
            {
                lexemes[i] = new CilLexeme(left.Start, right.End - left.Start, CilLexemeKind.Word);
                lexemes.RemoveAt(i + 1);
            }
        }

        return lexemes;
    }

    /// <summary>
    /// Whether a character may start a word: a letter, an underscore, a dot, a dollar, or an at sign.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <returns>True when a word may start here.</returns>
    public static bool IsWordStart(char c) => char.IsLetter(c) || c is '_' or '.' or '$' or '@';

    /// <summary>
    /// Whether a character may continue a word: the characters a type name may hold.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <returns>True when the character belongs to the word.</returns>
    public static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '.' or '_' or '`' or '/' or '$' or '@' or '+';

    private static void ScanCode(string line, int start, int end, List<CilLexeme> lexemes, ref bool byteList, ref bool byteListNext)
    {
        var i = start;
        while (i < end)
        {
            var c = line[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (byteList)
            {
                if (c == ')')
                {
                    byteList = false;
                    lexemes.Add(new CilLexeme(i, 1, CilLexemeKind.Punctuation));
                    i++;
                    continue;
                }

                if (IsHex(c) && i + 1 < end && IsHex(line[i + 1]) && (i + 2 >= end || !IsNameChar(line[i + 2])))
                {
                    lexemes.Add(new CilLexeme(i, 2, CilLexemeKind.Number));
                    i += 2;
                    continue;
                }
            }

            if (c == '(' && byteListNext)
            {
                byteList = true;
            }

            byteListNext = false;
            if (c == '[')
            {
                var close = line.IndexOf(']', i + 1);
                if (close > i + 1 && close < end && (char.IsLetter(line[i + 1]) || line[i + 1] is '_' or '.'))
                {
                    lexemes.Add(new CilLexeme(i, close - i + 1, CilLexemeKind.AssemblyHint));
                    i = close + 1;
                    continue;
                }

                lexemes.Add(new CilLexeme(i, 1, CilLexemeKind.Punctuation));
                i++;
                continue;
            }

            if (c == '!')
            {
                var j = i + 1;
                if (j < end && line[j] == '!')
                {
                    j++;
                }

                var k = j;
                while (k < end && IsNameChar(line[k]))
                {
                    k++;
                }

                lexemes.Add(new CilLexeme(i, Math.Max(1, k - i), k > j ? CilLexemeKind.GenericParameter : CilLexemeKind.Other));
                i = Math.Max(i + 1, k);
                continue;
            }

            if (c == ':')
            {
                var two = i + 1 < end && line[i + 1] == ':';
                lexemes.Add(new CilLexeme(i, two ? 2 : 1, two ? CilLexemeKind.DoubleColon : CilLexemeKind.Punctuation));
                i += two ? 2 : 1;
                continue;
            }

            if (c == '.' && i + 2 < end && line[i + 1] == '.' && line[i + 2] == '.')
            {
                lexemes.Add(new CilLexeme(i, 3, CilLexemeKind.Ellipsis));
                i += 3;
                continue;
            }

            if (char.IsDigit(c) || (c is '-' or '+' or '.' && i + 1 < end && char.IsDigit(line[i + 1])))
            {
                var k = ScanNumber(line, i, end);
                lexemes.Add(new CilLexeme(i, k - i, CilLexemeKind.Number));
                i = k;
                continue;
            }

            if (IsWordStart(c) || (c is '-' or '+' && i + 1 < end && char.IsLetter(line[i + 1])))
            {
                var k = i + 1;
                if (c == '.' && k < end && line[k] == '?' && (k + 1 >= end || char.IsWhiteSpace(line[k + 1])))
                {
                    // The .? command, the one dot-word with a character no name may hold.
                    k++;
                }
                else
                {
                    while (k < end && IsNameChar(line[k]))
                    {
                        k++;
                    }
                }

                lexemes.Add(new CilLexeme(i, k - i, CilLexemeKind.Word));
                byteListNext = line.AsSpan(i, k - i).SequenceEqual("bytearray");
                i = k;
                continue;
            }

            if (c == '=')
            {
                lexemes.Add(new CilLexeme(i, 1, CilLexemeKind.Punctuation));
                byteListNext = true;
                i++;
                continue;
            }

            lexemes.Add(new CilLexeme(i, 1, c is '(' or ')' or ']' or '<' or '>' or ',' or '&' or '*' or ';' or '{' or '}' or '|' ? CilLexemeKind.Punctuation : CilLexemeKind.Other));
            i++;
        }
    }

    private static int ScanNumber(string line, int i, int end)
    {
        var k = i;
        if (line[k] is '-' or '+')
        {
            k++;
        }

        if (k + 1 < end && line[k] == '0' && line[k + 1] is 'x' or 'X')
        {
            k += 2;
            while (k < end && (IsHex(line[k]) || line[k] == '_'))
            {
                k++;
            }

            return k;
        }

        if (k + 1 < end && line[k] == '0' && line[k + 1] is 'b' or 'B')
        {
            k += 2;
            while (k < end && line[k] is '0' or '1' or '_')
            {
                k++;
            }

            return k;
        }

        while (k < end && (char.IsDigit(line[k]) || line[k] == '_'))
        {
            k++;
        }

        if (k + 1 < end && line[k] == '.' && char.IsDigit(line[k + 1]))
        {
            k++;
            while (k < end && (char.IsDigit(line[k]) || line[k] == '_'))
            {
                k++;
            }
        }

        if (k < end && line[k] is 'e' or 'E')
        {
            var m = k + 1;
            if (m < end && line[m] is '+' or '-')
            {
                m++;
            }

            if (m < end && char.IsDigit(line[m]))
            {
                k = m;
                while (k < end && char.IsDigit(line[k]))
                {
                    k++;
                }
            }
        }

        return k;
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);
}
