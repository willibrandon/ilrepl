using System.Text;

namespace IlRepl.Protocol;

/// <summary>
/// Colours a line of IL by its grammar: the first word decides whether the line is an
/// instruction, a directive, a command, or a mistake, and the rest is read by the shape that
/// first word gives it. The same tokens light the editor's buffer, the echoed input, and every
/// listing, so a line reads the same wherever it appears. Only a <c>/* */</c> carries state from
/// line to line, as one flag.
/// </summary>
public sealed class CilTokenizer
{
    private static readonly string[] BlockKeywords = ["catch", "filter", "finally", "fault", "handler"];
    private static readonly string[] FloatWords = ["nan", "inf", "infinity", "-nan", "-inf", "-infinity", "+nan", "+inf", "+infinity"];

    private readonly Dictionary<string, CilOperandKind> _opcodes;
    private readonly HashSet<string> _directives;
    private readonly HashSet<string> _commands;
    private readonly HashSet<string> _keywords;
    private readonly HashSet<string> _primitives;
    private readonly Dictionary<string, CilOperandKind>.AlternateLookup<ReadOnlySpan<char>> _opcodeLookup;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _directiveLookup;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _commandLookup;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _keywordLookup;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _primitiveLookup;

    /// <summary>
    /// Initializes a tokenizer over a vocabulary.
    /// </summary>
    /// <param name="vocabulary">The words the tokenizer knows.</param>
    public CilTokenizer(CilVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        Vocabulary = vocabulary;
        _opcodes = new Dictionary<string, CilOperandKind>(vocabulary.Opcodes, StringComparer.Ordinal);
        _directives = new HashSet<string>(vocabulary.Directives, StringComparer.Ordinal);
        _commands = new HashSet<string>(vocabulary.Commands, StringComparer.Ordinal);
        _keywords = new HashSet<string>(vocabulary.Keywords, StringComparer.Ordinal);
        _primitives = new HashSet<string>(vocabulary.Primitives, StringComparer.Ordinal);
        _opcodeLookup = _opcodes.GetAlternateLookup<ReadOnlySpan<char>>();
        _directiveLookup = _directives.GetAlternateLookup<ReadOnlySpan<char>>();
        _commandLookup = _commands.GetAlternateLookup<ReadOnlySpan<char>>();
        _keywordLookup = _keywords.GetAlternateLookup<ReadOnlySpan<char>>();
        _primitiveLookup = _primitives.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>
    /// The words the tokenizer knows.
    /// </summary>
    public CilVocabulary Vocabulary { get; }

    /// <summary>
    /// Tokenizes one line, carrying an open <c>/* */</c> in and out so a buffer or a listing can
    /// be walked line by line.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="inBlockComment">Whether a <c>/*</c> from an earlier line is still open; updated for the next line.</param>
    /// <returns>The tokens, in order.</returns>
    public IReadOnlyList<CilToken> Tokenize(string line, ref bool inBlockComment)
    {
        ArgumentNullException.ThrowIfNull(line);
        var lexemes = CilScanner.Scan(line, ref inBlockComment);
        var tokens = new List<CilToken>(lexemes.Count);
        var code = new List<CilLexeme>(lexemes.Count);
        foreach (var lexeme in lexemes)
        {
            if (lexeme.Kind == CilLexemeKind.Comment)
            {
                tokens.Add(new CilToken(lexeme.Start, lexeme.Length, SpanStyle.Comment));
            }
            else
            {
                code.Add(lexeme);
            }
        }

        var reader = new CilLineReader(this, line, code, tokens);
        ReadLine(reader);
        tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        return tokens;
    }

    /// <summary>
    /// Tokenizes one line on its own: no comment is open when it starts.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The tokens, in order.</returns>
    public IReadOnlyList<CilToken> Tokenize(string line)
    {
        var closed = false;
        return Tokenize(line, ref closed);
    }

    /// <summary>
    /// Tokenizes one line into transcript spans that concatenate to the line: the text between
    /// tokens gets the plain style, and neighbouring spans of one style are merged.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="inBlockComment">Whether a <c>/*</c> from an earlier line is still open; updated for the next line.</param>
    /// <param name="plain">The style of the text between tokens.</param>
    /// <returns>The spans.</returns>
    public IReadOnlyList<TranscriptSpan> Spans(string line, ref bool inBlockComment, SpanStyle plain = SpanStyle.Default) =>
        ToSpans(line, Tokenize(line, ref inBlockComment), plain);

    /// <summary>
    /// Tokenizes one line on its own into transcript spans.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="plain">The style of the text between tokens.</param>
    /// <returns>The spans.</returns>
    public IReadOnlyList<TranscriptSpan> Spans(string line, SpanStyle plain = SpanStyle.Default)
    {
        var closed = false;
        return Spans(line, ref closed, plain);
    }

    /// <summary>
    /// Turns tokens back into transcript spans that concatenate to the line.
    /// </summary>
    /// <param name="line">The line the tokens came from.</param>
    /// <param name="tokens">The tokens, in order.</param>
    /// <param name="plain">The style of the text between tokens.</param>
    /// <returns>The spans.</returns>
    public static IReadOnlyList<TranscriptSpan> ToSpans(string line, IReadOnlyList<CilToken> tokens, SpanStyle plain = SpanStyle.Default)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(tokens);
        var spans = new List<TranscriptSpan>();
        var text = new StringBuilder();
        var style = plain;
        var position = 0;
        foreach (var token in tokens)
        {
            if (token.Start > position)
            {
                Append(line.AsSpan(position, token.Start - position), plain);
            }

            Append(line.AsSpan(token.Start, token.Length), token.Style);
            position = token.End;
        }

        if (position < line.Length)
        {
            Append(line.AsSpan(position), plain);
        }

        if (text.Length > 0)
        {
            spans.Add(new TranscriptSpan(text.ToString(), style));
        }

        return spans;

        void Append(ReadOnlySpan<char> piece, SpanStyle pieceStyle)
        {
            if (piece.Length == 0)
            {
                return;
            }

            if (pieceStyle != style && text.Length > 0)
            {
                spans.Add(new TranscriptSpan(text.ToString(), style));
                text.Clear();
            }

            style = pieceStyle;
            text.Append(piece);
        }
    }

    internal bool IsOpcode(ReadOnlySpan<char> word) => _opcodeLookup.ContainsKey(word);

    internal bool IsDirective(ReadOnlySpan<char> word) => _directiveLookup.Contains(word);

    internal bool IsCommand(ReadOnlySpan<char> word) => _commandLookup.Contains(word);

    internal bool IsKeyword(ReadOnlySpan<char> word) => _keywordLookup.Contains(word);

    internal bool IsPrimitive(ReadOnlySpan<char> word) => _primitiveLookup.Contains(word);

    internal CilOperandKind OperandKindOf(ReadOnlySpan<char> word) => _opcodeLookup.TryGetValue(word, out var kind) ? kind : CilOperandKind.None;

    internal static bool IsBlockKeywordWord(ReadOnlySpan<char> word) => IsBlockKeyword(word);

    private void ReadLine(CilLineReader r)
    {
        var i = 0;
        while (r.IsName(i) && r.IsPunct(i + 1, ':'))
        {
            r.EmitRange(i, i + 1, SpanStyle.Label);
            i += 2;
        }

        if (i >= r.Count)
        {
            return;
        }

        if (r.KindAt(i) == CilLexemeKind.Word)
        {
            var word = r.TextAt(i);
            if (IsOpcode(word))
            {
                r.Emit(i, SpanStyle.Opcode);
                i = ReadOperand(r, i + 1, OperandKindOf(word), word.EndsWith('.'));
            }
            else if (word[0] == '.')
            {
                if (IsDirective(word))
                {
                    r.Emit(i, SpanStyle.Directive);
                    i = ReadDirective(r, i + 1, word);
                }
                else if (IsCommand(word))
                {
                    r.Emit(i, SpanStyle.Command);
                    i = ReadCommand(r, i + 1, word);
                }
                else
                {
                    r.Emit(i, SpanStyle.Error);
                    i++;
                }
            }
            else if (IsBlockKeyword(word))
            {
                i = ReadBlockKeyword(r, i);
            }
            else
            {
                r.Emit(i, SpanStyle.Error);
                i++;
            }
        }
        else if (r.IsPunct(i, '}') || r.IsPunct(i, '{'))
        {
            r.Emit(i, SpanStyle.Punctuation);
            i++;
            if (r.KindAt(i) == CilLexemeKind.Word && IsBlockKeyword(r.TextAt(i)))
            {
                i = ReadBlockKeyword(r, i);
            }
        }
        else if (r.KindAt(i) == CilLexemeKind.Quoted)
        {
            r.Emit(i, SpanStyle.Error);
            i++;
        }

        r.ReadFallback(i);
    }

    private static int ReadBlockKeyword(CilLineReader r, int i)
    {
        var catches = r.IsWord(i, "catch");
        r.Emit(i, SpanStyle.Keyword);
        i++;
        return catches ? r.ReadType(i) : i;
    }

    private int ReadOperand(CilLineReader r, int i, CilOperandKind kind, bool prefix)
    {
        switch (kind)
        {
            case CilOperandKind.Branch:
                if (r.IsName(i))
                {
                    r.Emit(i, SpanStyle.Label);
                    i++;
                }

                break;
            case CilOperandKind.Switch:
                if (r.IsPunct(i, '('))
                {
                    r.Emit(i, SpanStyle.Punctuation);
                    i++;
                    while (i < r.Count && !r.IsPunct(i, ')'))
                    {
                        if (r.IsName(i))
                        {
                            r.Emit(i, SpanStyle.Label);
                        }
                        else
                        {
                            r.EmitFallback(i);
                        }

                        i++;
                    }

                    if (r.IsPunct(i, ')'))
                    {
                        r.Emit(i, SpanStyle.Punctuation);
                        i++;
                    }
                }

                break;
            case CilOperandKind.Integer:
                if (r.KindAt(i) is CilLexemeKind.Number or CilLexemeKind.Quoted)
                {
                    r.Emit(i, SpanStyle.Number);
                    i++;
                }

                break;
            case CilOperandKind.Float:
                if (r.KindAt(i) == CilLexemeKind.Number || (r.KindAt(i) == CilLexemeKind.Word && IsFloatWord(r.TextAt(i))))
                {
                    r.Emit(i, SpanStyle.Number);
                    i++;
                }
                else if ((r.IsWord(i, "float32") || r.IsWord(i, "float64")) && r.IsPunct(i + 1, '('))
                {
                    var close = r.Matching(i + 1);
                    if (close > 0)
                    {
                        r.EmitRange(i, close, SpanStyle.Number);
                        i = close + 1;
                    }
                }

                break;
            case CilOperandKind.Variable:
                if (r.KindAt(i) == CilLexemeKind.Number)
                {
                    r.Emit(i, SpanStyle.Number);
                    i++;
                }
                else if (r.IsName(i))
                {
                    r.Emit(i, SpanStyle.Default);
                    i++;
                }

                break;
            case CilOperandKind.String:
                if (r.KindAt(i) is CilLexemeKind.String or CilLexemeKind.Quoted)
                {
                    r.Emit(i, SpanStyle.String);
                    i++;
                }

                break;
            case CilOperandKind.Type:
                i = r.ReadType(i);
                break;
            case CilOperandKind.Member:
            case CilOperandKind.Field:
                i = r.ReadMemberReference(i);
                break;
            case CilOperandKind.Token:
                if (r.IsWord(i, "method") || r.IsWord(i, "field"))
                {
                    r.Emit(i, SpanStyle.Keyword);
                    i = r.ReadMemberReference(i + 1);
                }
                else
                {
                    i = r.ReadType(i);
                }

                break;
            case CilOperandKind.Signature:
                i = r.ReadCallingConvention(i);
                i = r.ReadType(i);
                i = r.ReadParameterList(i);
                break;
            default:
                break;
        }

        if (prefix && r.IsOpcode(i))
        {
            var word = r.TextAt(i);
            r.Emit(i, SpanStyle.Opcode);
            i = ReadOperand(r, i + 1, OperandKindOf(word), word.EndsWith('.'));
        }

        return i;
    }

    private int ReadDirective(CilLineReader r, int i, ReadOnlySpan<char> directive)
    {
        switch (directive)
        {
            case ".locals":
                if (r.IsWord(i, "init"))
                {
                    r.Emit(i, SpanStyle.Keyword);
                    i++;
                }

                return r.ReadParameterList(i);
            case ".args":
            case ".typeargs":
                return r.ReadParameterList(i);
            case ".typeparams":
                if (!r.IsPunct(i, '('))
                {
                    return i;
                }

                r.Emit(i, SpanStyle.Punctuation);
                i++;
                while (i < r.Count && !r.IsPunct(i, ')'))
                {
                    r.Emit(i, r.IsName(i) ? SpanStyle.Type : r.IsPunct(i, ',') ? SpanStyle.Punctuation : SpanStyle.Default);
                    i++;
                }

                if (r.IsPunct(i, ')'))
                {
                    r.Emit(i, SpanStyle.Punctuation);
                    i++;
                }

                return i;
            case ".maxstack":
            case ".pack":
            case ".size":
                return r.ReadLiteral(i);
            case ".method":
                i = r.ReadModifiers(i);
                i = r.ReadType(i);
                if (r.IsName(i))
                {
                    r.Emit(i, SpanStyle.Member);
                    i++;
                }

                i = r.ReadGenericParameters(i);
                i = r.ReadParameterList(i);
                return r.ReadModifiers(i);
            case ".class":
                i = r.ReadModifiers(i, stopAtType: false);
                if (r.IsName(i))
                {
                    r.Emit(i, SpanStyle.Type);
                    i++;
                }

                i = r.ReadGenericParameters(i);
                if (r.IsWord(i, "extends"))
                {
                    r.Emit(i, SpanStyle.Keyword);
                    i = r.ReadType(i + 1);
                }

                if (r.IsWord(i, "implements"))
                {
                    r.Emit(i, SpanStyle.Keyword);
                    i++;
                    while (i < r.Count)
                    {
                        var next = r.ReadType(i);
                        if (next == i)
                        {
                            break;
                        }

                        i = next;
                        if (!r.IsPunct(i, ','))
                        {
                            break;
                        }

                        r.Emit(i, SpanStyle.Punctuation);
                        i++;
                    }
                }

                return i;
            case ".field":
            case ".event":
            case ".property":
                i = r.ReadModifiers(i);
                i = r.ReadType(i);
                if (r.IsName(i))
                {
                    r.Emit(i, SpanStyle.Member);
                    i++;
                }

                i = r.ReadParameterList(i);
                if (r.IsPunct(i, '='))
                {
                    r.Emit(i, SpanStyle.Punctuation);
                    i = ReadFieldInitializer(r, i + 1);
                }

                return i;
            case ".get":
            case ".set":
            case ".other":
            case ".addon":
            case ".removeon":
            case ".fire":
                return r.ReadMemberReference(i);
            case ".override":
                if (r.IsWord(i, "method"))
                {
                    r.Emit(i, SpanStyle.Keyword);
                    i++;
                }

                i = r.ReadMemberReference(i);
                if (r.IsWord(i, "with"))
                {
                    r.Emit(i, SpanStyle.Keyword);
                    i++;
                    if (r.IsWord(i, "method"))
                    {
                        r.Emit(i, SpanStyle.Keyword);
                        i++;
                    }

                    i = r.ReadMemberReference(i);
                }

                return i;
            case ".param":
                if (r.IsPunct(i, '['))
                {
                    r.Emit(i, SpanStyle.Punctuation);
                    r.Emit(i + 1, SpanStyle.Number);
                    r.Emit(i + 2, SpanStyle.Punctuation);
                    i += 3;
                }

                if (r.IsPunct(i, '='))
                {
                    r.Emit(i, SpanStyle.Punctuation);
                    i = ReadFieldInitializer(r, i + 1);
                }

                return i;
            case ".custom":
                i = r.ReadMemberReference(i);
                if (r.IsPunct(i, '='))
                {
                    r.Emit(i, SpanStyle.Punctuation);
                    i++;
                    i = ReadAttributeBlob(r, i);
                }

                return i;
            default:
                return i;
        }
    }

    private static int ReadFieldInitializer(CilLineReader r, int i)
    {
        if (r.IsWord(i, "bytearray"))
        {
            r.Emit(i, SpanStyle.Keyword);
            return i + 1;
        }

        if (r.IsPrimitive(i) && r.IsPunct(i + 1, '('))
        {
            r.Emit(i, SpanStyle.Type);
            r.Emit(i + 1, SpanStyle.Punctuation);
            i = r.ReadLiteral(i + 2);
            if (r.IsPunct(i, ')'))
            {
                r.Emit(i, SpanStyle.Punctuation);
                i++;
            }

            return i;
        }

        return r.ReadLiteral(i);
    }

    private int ReadAttributeBlob(CilLineReader r, int i)
    {
        if (!r.IsPunct(i, '{'))
        {
            return i;
        }

        r.Emit(i, SpanStyle.Punctuation);
        i++;
        while (i < r.Count && !r.IsPunct(i, '}'))
        {
            if (r.KindAt(i) == CilLexemeKind.Word && (IsPrimitive(r.TextAt(i)) || r.IsWord(i, "type") || r.IsWord(i, "object")) && r.IsPunct(i + 1, '('))
            {
                r.Emit(i, r.IsWord(i, "type") || r.IsWord(i, "object") ? SpanStyle.Keyword : SpanStyle.Type);
                r.Emit(i + 1, SpanStyle.Punctuation);
                i += 2;
                if (r.KindAt(i) == CilLexemeKind.Quoted)
                {
                    r.Emit(i, SpanStyle.String);
                    i++;
                }
                else
                {
                    var next = r.ReadLiteral(i);
                    i = next == i ? r.ReadType(i) : next;
                }

                if (r.IsPunct(i, ')'))
                {
                    r.Emit(i, SpanStyle.Punctuation);
                    i++;
                }

                continue;
            }

            if (r.IsWord(i, "field") || r.IsWord(i, "property"))
            {
                r.Emit(i, SpanStyle.Keyword);
                i++;
                continue;
            }

            r.EmitFallback(i);
            i++;
        }

        if (r.IsPunct(i, '}'))
        {
            r.Emit(i, SpanStyle.Punctuation);
            i++;
        }

        return i;
    }

    private static int ReadCommand(CilLineReader r, int i, ReadOnlySpan<char> command)
    {
        if (command.SequenceEqual(".dis") || command.SequenceEqual(".disassemble"))
        {
            return r.ReadMemberReference(i);
        }

        r.ReadPlain(i);
        return r.Count;
    }

    private static bool IsBlockKeyword(ReadOnlySpan<char> word)
    {
        foreach (var keyword in BlockKeywords)
        {
            if (word.SequenceEqual(keyword))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFloatWord(ReadOnlySpan<char> word)
    {
        foreach (var candidate in FloatWords)
        {
            if (word.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
