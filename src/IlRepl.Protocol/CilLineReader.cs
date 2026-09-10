namespace IlRepl.Protocol;

/// <summary>
/// Reads the grammar of one line over its lexemes and emits tokens as it goes: types with their
/// modifiers and suffixes, parameter lists, member references, and the fallback for anything the
/// grammar has no shape for. Types are consumed as grammar; a name is what is left over.
/// </summary>
internal sealed class CilLineReader
{
    private static readonly string[] CallingConventions = ["instance", "explicit", "vararg", "unmanaged", "cdecl", "stdcall", "thiscall", "fastcall", "default", "winapi", "platformapi"];
    private static readonly string[] ParameterAttributes = ["[in]", "[out]", "[opt]"];

    private readonly CilTokenizer _tokenizer;
    private readonly string _line;
    private readonly List<CilLexeme> _lexemes;
    private readonly List<CilToken> _tokens;

    /// <summary>
    /// Initializes a reader over a line's lexemes.
    /// </summary>
    /// <param name="tokenizer">The tokenizer, for its vocabulary.</param>
    /// <param name="line">The line.</param>
    /// <param name="lexemes">The lexemes, comments left out.</param>
    /// <param name="tokens">Where tokens go.</param>
    public CilLineReader(CilTokenizer tokenizer, string line, List<CilLexeme> lexemes, List<CilToken> tokens)
    {
        _tokenizer = tokenizer;
        _line = line;
        _lexemes = lexemes;
        _tokens = tokens;
    }

    /// <summary>
    /// How many lexemes the line has.
    /// </summary>
    public int Count => _lexemes.Count;

    /// <summary>
    /// The kind of the lexeme at an index, or <see cref="CilLexemeKind.Other"/> past the end.
    /// </summary>
    /// <param name="i">The index.</param>
    /// <returns>The kind.</returns>
    public CilLexemeKind KindAt(int i) => i >= 0 && i < _lexemes.Count ? _lexemes[i].Kind : CilLexemeKind.Other;

    /// <summary>
    /// The index in the line where the lexeme at an index starts, or the line's length past the end.
    /// </summary>
    /// <param name="i">The index.</param>
    /// <returns>The start offset.</returns>
    public int StartOf(int i) => i >= 0 && i < _lexemes.Count ? _lexemes[i].Start : _line.Length;

    /// <summary>
    /// The index in the line just past the lexeme at an index, or the line's length past the end.
    /// </summary>
    /// <param name="i">The index.</param>
    /// <returns>The end offset.</returns>
    public int EndOf(int i) => i >= 0 && i < _lexemes.Count ? _lexemes[i].End : _line.Length;

    /// <summary>
    /// The line the lexemes were cut from.
    /// </summary>
    public string Line => _line;

    /// <summary>
    /// The text of the lexeme at an index, or empty past the end.
    /// </summary>
    /// <param name="i">The index.</param>
    /// <returns>The text.</returns>
    public ReadOnlySpan<char> TextAt(int i) => i >= 0 && i < _lexemes.Count ? _line.AsSpan(_lexemes[i].Start, _lexemes[i].Length) : [];

    /// <summary>
    /// Whether the lexeme at an index is a word with the given text.
    /// </summary>
    /// <param name="i">The index.</param>
    /// <param name="text">The text.</param>
    /// <returns>True when it is.</returns>
    public bool IsWord(int i, string text) => KindAt(i) == CilLexemeKind.Word && TextAt(i).SequenceEqual(text);

    /// <summary>
    /// Whether the lexeme at an index is the given punctuation character.
    /// </summary>
    /// <param name="i">The index.</param>
    /// <param name="c">The character.</param>
    /// <returns>True when it is.</returns>
    public bool IsPunct(int i, char c) => KindAt(i) == CilLexemeKind.Punctuation && _line[_lexemes[i].Start] == c;

    /// <summary>
    /// Whether the lexeme at an index could be a name: a word or a quoted name.
    /// </summary>
    /// <param name="i">The index.</param>
    /// <returns>True when it could.</returns>
    public bool IsName(int i) => KindAt(i) is CilLexemeKind.Word or CilLexemeKind.Quoted;

    /// <summary>
    /// Whether the word at an index is an ILAsm keyword.
    /// </summary>
    /// <param name="i">The index.</param>
    /// <returns>True when it is.</returns>
    public bool IsKeyword(int i) => KindAt(i) == CilLexemeKind.Word && _tokenizer.IsKeyword(TextAt(i));

    /// <summary>
    /// Whether the word at an index names a primitive type.
    /// </summary>
    /// <param name="i">The index.</param>
    /// <returns>True when it does.</returns>
    public bool IsPrimitive(int i) => KindAt(i) == CilLexemeKind.Word && _tokenizer.IsPrimitive(TextAt(i));

    /// <summary>
    /// Whether the word at an index is an opcode.
    /// </summary>
    /// <param name="i">The index.</param>
    /// <returns>True when it is.</returns>
    public bool IsOpcode(int i) => KindAt(i) == CilLexemeKind.Word && _tokenizer.IsOpcode(TextAt(i));

    /// <summary>
    /// Emits one lexeme as a token.
    /// </summary>
    /// <param name="i">The index.</param>
    /// <param name="style">The style.</param>
    public void Emit(int i, SpanStyle style)
    {
        if (i >= 0 && i < _lexemes.Count)
        {
            _tokens.Add(new CilToken(_lexemes[i].Start, _lexemes[i].Length, style));
        }
    }

    /// <summary>
    /// Emits a run of lexemes as one token, the text between them included.
    /// </summary>
    /// <param name="from">The first index.</param>
    /// <param name="to">The last index.</param>
    /// <param name="style">The style.</param>
    public void EmitRange(int from, int to, SpanStyle style)
    {
        to = Math.Min(to, _lexemes.Count - 1);
        if (from < 0 || from > to)
        {
            return;
        }

        // A comment between the lexemes already has its token, so the run is emitted in the
        // pieces around it and no two tokens overlap.
        var start = _lexemes[from].Start;
        var end = _lexemes[to].End;
        var comments = _tokens.Where(t => t.Style == SpanStyle.Comment && t.Start < end && t.Start + t.Length > start).OrderBy(t => t.Start).ToList();
        foreach (var comment in comments)
        {
            if (comment.Start > start)
            {
                _tokens.Add(new CilToken(start, comment.Start - start, style));
            }

            start = Math.Max(start, comment.Start + comment.Length);
        }

        if (end > start)
        {
            _tokens.Add(new CilToken(start, end - start, style));
        }
    }

    /// <summary>
    /// Finds the lexeme that closes the bracket opened at an index.
    /// </summary>
    /// <param name="i">The index of the opening bracket.</param>
    /// <returns>The index of the closing bracket, or -1 when it never closes.</returns>
    public int Matching(int i)
    {
        if (KindAt(i) != CilLexemeKind.Punctuation)
        {
            return -1;
        }

        var open = _line[_lexemes[i].Start];
        var close = open switch { '(' => ')', '<' => '>', '[' => ']', '{' => '}', _ => '\0' };
        if (close == '\0')
        {
            return -1;
        }

        var depth = 0;
        for (var j = i; j < _lexemes.Count; j++)
        {
            if (_lexemes[j].Kind != CilLexemeKind.Punctuation)
            {
                continue;
            }

            var c = _line[_lexemes[j].Start];
            if (c == open)
            {
                depth++;
            }
            else if (c == close && --depth == 0)
            {
                return j;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads one type: <c>class</c> or <c>valuetype</c>, an assembly hint, the name, a primitive
    /// (<c>native int</c> included), a generic parameter, or a function pointer, then any generic
    /// arguments, array bounds, pointer and reference marks, <c>pinned</c>, and modifiers.
    /// </summary>
    /// <param name="i">Where the type starts.</param>
    /// <returns>The index just past the type, or <paramref name="i"/> when there is no type here.</returns>
    public int ReadType(int i)
    {
        var start = i;
        if (IsWord(i, "class") || IsWord(i, "valuetype"))
        {
            Emit(i, SpanStyle.Keyword);
            i++;
        }

        if (KindAt(i) == CilLexemeKind.AssemblyHint)
        {
            Emit(i, SpanStyle.Dim);
            i++;
        }

        if (IsWord(i, "method"))
        {
            // A function pointer: method <convention> <return type> *(<types>).
            Emit(i, SpanStyle.Keyword);
            i = ReadCallingConvention(i + 1);
            i = ReadType(i);
            if (IsPunct(i, '*'))
            {
                Emit(i, SpanStyle.Punctuation);
                i++;
            }

            if (IsPunct(i, '('))
            {
                i = ReadParameterList(i);
            }

            return ReadTypeSuffixes(i);
        }

        if (KindAt(i) == CilLexemeKind.Word)
        {
            while ((IsWord(i, "native") || IsWord(i, "unsigned")) && (IsWord(i + 1, "native") || IsWord(i + 1, "unsigned") || IsPrimitive(i + 1)))
            {
                Emit(i, SpanStyle.Type);
                i++;
            }

            Emit(i, SpanStyle.Type);
            i++;
        }
        else if (KindAt(i) is CilLexemeKind.Quoted or CilLexemeKind.GenericParameter)
        {
            Emit(i, SpanStyle.Type);
            i++;
        }
        else if (i == start)
        {
            return start;
        }

        return ReadTypeSuffixes(i);
    }

    /// <summary>
    /// Reads a parenthesised list of parameters or locals: each item is an optional slot or
    /// attribute, a type, an optional name, and for a cell argument an optional default.
    /// </summary>
    /// <param name="i">The index of the opening parenthesis.</param>
    /// <returns>The index just past the closing parenthesis.</returns>
    public int ReadParameterList(int i)
    {
        if (!IsPunct(i, '('))
        {
            return i;
        }

        Emit(i, SpanStyle.Punctuation);
        i++;
        while (i < Count && !IsPunct(i, ')'))
        {
            var before = i;
            if (IsPunct(i, ','))
            {
                Emit(i, SpanStyle.Punctuation);
                i++;
                continue;
            }

            if (KindAt(i) == CilLexemeKind.Ellipsis)
            {
                Emit(i, SpanStyle.Punctuation);
                i++;
                continue;
            }

            if (IsPunct(i, '[') && KindAt(i + 1) == CilLexemeKind.Number && IsPunct(i + 2, ']'))
            {
                Emit(i, SpanStyle.Punctuation);
                Emit(i + 1, SpanStyle.Number);
                Emit(i + 2, SpanStyle.Punctuation);
                i += 3;
            }

            while (KindAt(i) == CilLexemeKind.AssemblyHint && IsParameterAttribute(i))
            {
                Emit(i, SpanStyle.Keyword);
                i++;
            }

            i = ReadType(i);
            if (IsName(i) && (i + 1 >= Count || IsPunct(i + 1, ',') || IsPunct(i + 1, ')') || IsPunct(i + 1, '=')))
            {
                Emit(i, SpanStyle.Default);
                i++;
            }

            if (IsPunct(i, '='))
            {
                Emit(i, SpanStyle.Punctuation);
                i = ReadLiteral(i + 1);
            }

            if (i == before)
            {
                EmitFallback(i);
                i++;
            }
        }

        if (IsPunct(i, ')'))
        {
            Emit(i, SpanStyle.Punctuation);
            i++;
        }

        return i;
    }

    /// <summary>
    /// Reads a method or field reference: the calling convention, the return or field type, then
    /// either a bare name or a declaring type, <c>::</c>, and the name, then generic arguments and
    /// the parameter types.
    /// </summary>
    /// <param name="i">Where the reference starts.</param>
    /// <returns>The index just past it.</returns>
    public int ReadMemberReference(int i)
    {
        i = ReadCallingConvention(i);
        if (!(IsName(i) && IsBareMemberName(i) && !IsPrimitive(i)))
        {
            // A name followed by the parameter list, or by nothing, is the member itself with no
            // return type in front of it: `.dis Fib`.
            i = ReadType(i);
        }

        if (IsName(i) && IsBareMemberName(i))
        {
            Emit(i, SpanStyle.Member);
            i++;
        }
        else if (IsName(i) || KindAt(i) is CilLexemeKind.AssemblyHint or CilLexemeKind.GenericParameter)
        {
            i = ReadType(i);
            if (KindAt(i) == CilLexemeKind.DoubleColon)
            {
                Emit(i, SpanStyle.Punctuation);
                i++;
                if (IsName(i))
                {
                    Emit(i, SpanStyle.Member);
                    i++;
                }
            }
        }
        else if (KindAt(i) == CilLexemeKind.DoubleColon)
        {
            // The declaring type was read as the return type: `.dis String::Trim` style references.
            Emit(i, SpanStyle.Punctuation);
            i++;
            if (IsName(i))
            {
                Emit(i, SpanStyle.Member);
                i++;
            }
        }

        if (IsPunct(i, '<'))
        {
            i = ReadGenericArguments(i);
        }

        if (IsPunct(i, '('))
        {
            i = ReadParameterList(i);
        }

        return i;
    }

    /// <summary>
    /// Reads calling-convention keywords: <c>instance</c>, <c>vararg</c>, <c>unmanaged cdecl</c>, and the rest.
    /// </summary>
    /// <param name="i">Where they start.</param>
    /// <returns>The index just past them.</returns>
    public int ReadCallingConvention(int i)
    {
        while (KindAt(i) == CilLexemeKind.Word && IsOneOf(i, CallingConventions))
        {
            Emit(i, SpanStyle.Keyword);
            i++;
        }

        return i;
    }

    /// <summary>
    /// Reads a literal: a number, a string, a quoted character, or <c>true</c>, <c>false</c>, <c>null</c>, <c>nullref</c>.
    /// </summary>
    /// <param name="i">Where it starts.</param>
    /// <returns>The index just past it.</returns>
    public int ReadLiteral(int i)
    {
        switch (KindAt(i))
        {
            case CilLexemeKind.Number:
                Emit(i, SpanStyle.Number);
                return i + 1;
            case CilLexemeKind.String:
                Emit(i, SpanStyle.String);
                return i + 1;
            case CilLexemeKind.Quoted:
                Emit(i, SpanStyle.Number);
                return i + 1;
            case CilLexemeKind.Word when IsKeyword(i):
                Emit(i, SpanStyle.Keyword);
                return i + 1;
            default:
                return i;
        }
    }

    /// <summary>
    /// Emits one lexeme by its shape alone, for text the grammar has no shape for.
    /// </summary>
    /// <param name="i">The index.</param>
    public void EmitFallback(int i)
    {
        switch (KindAt(i))
        {
            case CilLexemeKind.Number:
                Emit(i, SpanStyle.Number);
                break;
            case CilLexemeKind.String:
                Emit(i, SpanStyle.String);
                break;
            case CilLexemeKind.Quoted:
            case CilLexemeKind.Other:
                Emit(i, SpanStyle.Default);
                break;
            case CilLexemeKind.Comment:
                Emit(i, SpanStyle.Comment);
                break;
            case CilLexemeKind.Punctuation:
            case CilLexemeKind.DoubleColon:
            case CilLexemeKind.Ellipsis:
                Emit(i, SpanStyle.Punctuation);
                break;
            case CilLexemeKind.GenericParameter:
                Emit(i, SpanStyle.Type);
                break;
            case CilLexemeKind.AssemblyHint:
                Emit(i, IsParameterAttribute(i) ? SpanStyle.Keyword : SpanStyle.Dim);
                break;
            case CilLexemeKind.Word:
                Emit(i, WordFallback(i));
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Emits everything from an index to the end of the line by shape alone.
    /// </summary>
    /// <param name="i">The first index.</param>
    public void ReadFallback(int i)
    {
        for (; i < Count; i++)
        {
            EmitFallback(i);
        }
    }

    /// <summary>
    /// Emits everything from an index to the end of the line as plain text, for a command's arguments.
    /// </summary>
    /// <param name="i">The first index.</param>
    public void ReadPlain(int i)
    {
        for (; i < Count; i++)
        {
            Emit(i, KindAt(i) == CilLexemeKind.String ? SpanStyle.String : SpanStyle.Default);
        }
    }

    /// <summary>
    /// Reads a list of generic arguments, each a type, between angle brackets.
    /// </summary>
    /// <param name="i">The index of the opening bracket.</param>
    /// <returns>The index just past the closing bracket.</returns>
    public int ReadGenericArguments(int i)
    {
        if (!IsPunct(i, '<'))
        {
            return i;
        }

        Emit(i, SpanStyle.Punctuation);
        i++;
        while (i < Count && !IsPunct(i, '>'))
        {
            if (IsPunct(i, ','))
            {
                Emit(i, SpanStyle.Punctuation);
                i++;
                continue;
            }

            var next = ReadType(i);
            if (next == i)
            {
                EmitFallback(i);
                next = i + 1;
            }

            i = next;
        }

        if (IsPunct(i, '>'))
        {
            Emit(i, SpanStyle.Punctuation);
            i++;
        }

        return i;
    }

    /// <summary>
    /// Reads the generic parameters of a declaration: names, constraint keywords, and constraint
    /// types in parentheses, between angle brackets.
    /// </summary>
    /// <param name="i">The index of the opening bracket.</param>
    /// <returns>The index just past the closing bracket.</returns>
    public int ReadGenericParameters(int i)
    {
        if (!IsPunct(i, '<'))
        {
            return i;
        }

        Emit(i, SpanStyle.Punctuation);
        i++;
        while (i < Count && !IsPunct(i, '>'))
        {
            if (IsPunct(i, '('))
            {
                var close = Matching(i);
                Emit(i, SpanStyle.Punctuation);
                i++;
                while (i < Count && i != close && !IsPunct(i, ')'))
                {
                    if (IsPunct(i, ','))
                    {
                        Emit(i, SpanStyle.Punctuation);
                        i++;
                        continue;
                    }

                    var next = ReadType(i);
                    if (next == i)
                    {
                        EmitFallback(i);
                        next = i + 1;
                    }

                    i = next;
                }

                if (IsPunct(i, ')'))
                {
                    Emit(i, SpanStyle.Punctuation);
                    i++;
                }

                continue;
            }

            if (IsKeyword(i) && !IsName(i + 1) && !IsPunct(i + 1, ',') && !IsPunct(i + 1, '>') || IsWord(i, "class") || IsWord(i, "valuetype") || IsWord(i, "byreflike") || IsWord(i, ".ctor"))
            {
                Emit(i, SpanStyle.Keyword);
                i++;
                continue;
            }

            if (IsName(i))
            {
                Emit(i, SpanStyle.Type);
                i++;
                continue;
            }

            EmitFallback(i);
            i++;
        }

        if (IsPunct(i, '>'))
        {
            Emit(i, SpanStyle.Punctuation);
            i++;
        }

        return i;
    }

    /// <summary>
    /// Reads the modifiers of a declaration, keyword by keyword, and stops where a type begins.
    /// A modifier with a parenthesised argument, such as <c>pinvokeimpl(...)</c>, is read whole.
    /// </summary>
    /// <param name="i">Where the modifiers start.</param>
    /// <returns>The index just past them.</returns>
    public int ReadModifiers(int i) => ReadModifiers(i, stopAtType: true);

    /// <summary>
    /// Reads the modifiers of a declaration, keyword by keyword. A <c>.class</c> header has no type
    /// between its modifiers and its name, so its reading does not stop where a type could begin.
    /// </summary>
    /// <param name="i">Where the modifiers start.</param>
    /// <param name="stopAtType">Whether to stop where a type could begin.</param>
    /// <returns>The index just past them.</returns>
    public int ReadModifiers(int i, bool stopAtType)
    {
        while (KindAt(i) == CilLexemeKind.Word && IsKeyword(i) && !(stopAtType && IsTypeStart(i)))
        {
            Emit(i, SpanStyle.Keyword);
            i++;
            if (IsPunct(i, '('))
            {
                var close = Matching(i);
                if (close < 0)
                {
                    break;
                }

                for (var j = i; j <= close; j++)
                {
                    EmitFallback(j);
                }

                i = close + 1;
            }
        }

        return i;
    }

    /// <summary>
    /// Whether a type could begin at an index: a primitive, <c>class</c>, <c>valuetype</c>, <c>method</c>, or a multi-word primitive.
    /// </summary>
    /// <param name="i">The index.</param>
    /// <returns>True when a type could begin here.</returns>
    internal bool IsTypeStart(int i) =>
        IsPrimitive(i)
        || IsWord(i, "class") || IsWord(i, "valuetype") || IsWord(i, "method")
        || ((IsWord(i, "native") || IsWord(i, "unsigned")) && (IsPrimitive(i + 1) || IsWord(i + 1, "native") || IsWord(i + 1, "unsigned")));

    private int ReadTypeSuffixes(int i)
    {
        while (i < Count)
        {
            if (IsPunct(i, '<'))
            {
                i = ReadGenericArguments(i);
                continue;
            }

            if (IsPunct(i, '['))
            {
                var close = Matching(i);
                if (close < 0)
                {
                    break;
                }

                for (var j = i; j <= close; j++)
                {
                    Emit(j, KindAt(j) == CilLexemeKind.Number ? SpanStyle.Number : SpanStyle.Punctuation);
                }

                i = close + 1;
                continue;
            }

            if (IsPunct(i, '*') || IsPunct(i, '&'))
            {
                Emit(i, SpanStyle.Punctuation);
                i++;
                continue;
            }

            if (IsWord(i, "pinned"))
            {
                Emit(i, SpanStyle.Keyword);
                i++;
                continue;
            }

            if (IsWord(i, "modreq") || IsWord(i, "modopt"))
            {
                Emit(i, SpanStyle.Keyword);
                i++;
                if (IsPunct(i, '('))
                {
                    Emit(i, SpanStyle.Punctuation);
                    i = ReadType(i + 1);
                    if (IsPunct(i, ')'))
                    {
                        Emit(i, SpanStyle.Punctuation);
                        i++;
                    }
                }

                continue;
            }

            break;
        }

        return i;
    }

    /// <summary>
    /// Recognizes a standalone member name followed by an optional generic argument list and parameter list.
    /// </summary>
    /// <remarks>
    /// Whether the name at an index is a member on its own: followed by its parameter list, by
    /// generic arguments and then the list, or by nothing.
    /// </remarks>
    /// <param name="i">The index.</param>
    /// <returns>True for a bare member name.</returns>
    internal bool IsBareMemberName(int i)
    {
        if (i + 1 >= Count || IsPunct(i + 1, '('))
        {
            return true;
        }

        if (IsPunct(i + 1, '<'))
        {
            var close = Matching(i + 1);
            return close >= 0 && IsPunct(close + 1, '(');
        }

        return false;
    }

    private bool IsParameterAttribute(int i) => IsOneOf(i, ParameterAttributes);

    private bool IsOneOf(int i, string[] words)
    {
        var text = TextAt(i);
        foreach (var word in words)
        {
            if (text.SequenceEqual(word))
            {
                return true;
            }
        }

        return false;
    }

    private SpanStyle WordFallback(int i)
    {
        var text = TextAt(i);
        if (IsIlLabel(text))
        {
            return SpanStyle.Label;
        }

        if (_tokenizer.IsPrimitive(text))
        {
            return SpanStyle.Type;
        }

        if (_tokenizer.IsKeyword(text))
        {
            return SpanStyle.Keyword;
        }

        if (text.IndexOfAny(".`/") >= 0)
        {
            return SpanStyle.Type;
        }

        return SpanStyle.Default;
    }

    private static bool IsIlLabel(ReadOnlySpan<char> text)
    {
        if (text.Length != 7 || !text.StartsWith("IL_"))
        {
            return false;
        }

        for (var i = 3; i < 7; i++)
        {
            if (!char.IsAsciiHexDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
