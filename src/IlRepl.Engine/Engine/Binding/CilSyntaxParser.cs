using System.Globalization;
using System.Text;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Parses ILAsm type, member, and signature syntax while preserving source positions without resolving names.
/// </summary>
/// <remarks>
/// Reads the type and member grammar of ILAsm into syntax trees that keep their positions, and
/// looks nothing up. The type grammar: <c>[class|valuetype] ([asm])Name[&lt;Args&gt;]</c>, primitives,
/// <c>!N</c> and <c>!!N</c>, <c>method RetType *(Params)</c>, and the suffixes <c>[]</c>, <c>[,]</c>,
/// <c>&amp;</c>, <c>*</c>, <c>pinned</c>, <c>modreq(T)</c>, <c>modopt(T)</c>. The member grammar:
/// <c>[instance] [vararg] [RetType] Declaring::Name[&lt;Args&gt;][(Params)]</c>, and the session form
/// <c>[RetType] Name(Params)</c>. The runtime parsers and the completer read through this one
/// grammar, so a spelling means the same thing to both.
/// </remarks>
public static partial class CilSyntaxParser
{
    /// <summary>
    /// Parses a complete type expression.
    /// </summary>
    /// <param name="text">The type in IL syntax.</param>
    /// <returns>The syntax.</returns>
    /// <exception cref="ReplException">The text is not a type.</exception>
    public static TypeSyntax ParseType(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var pos = 0;
        var syntax = ParseTypeAt(text, ref pos);
        SkipWhitespace(text, ref pos);
        if (pos != text.Length)
        {
            throw new ReplException($"unexpected '{text[pos..]}' after type");
        }

        return syntax;
    }

    /// <summary>
    /// Parses the type between two positions, which must hold nothing else.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="start">The index the type starts at.</param>
    /// <param name="end">The index the type must end by.</param>
    /// <returns>The syntax.</returns>
    /// <exception cref="ReplException">The range is not a type.</exception>
    public static TypeSyntax ParseTypeIn(string s, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(s);
        var pos = start;
        SkipWhitespace(s, ref pos);
        if (pos >= end)
        {
            throw new ReplException("expected a type");
        }

        var syntax = ParseTypeAt(s, ref pos, end);
        SkipWhitespace(s, ref pos);
        if (pos < end)
        {
            throw new ReplException($"unexpected '{s[pos..end].Trim()}' after type");
        }

        return syntax;
    }

    /// <summary>
    /// Parses a type starting at <paramref name="pos"/> and advances past it.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="pos">The position to start at; updated to the first character after the type.</param>
    /// <returns>The syntax.</returns>
    /// <exception cref="ReplException">The text is not a type.</exception>
    public static TypeSyntax ParseTypeAt(string s, ref int pos)
    {
        ArgumentNullException.ThrowIfNull(s);
        return ParseTypeAt(s, ref pos, s.Length);
    }

    private static TypeSyntax ParseTypeAt(string s, ref int pos, int end)
    {
        SkipWhitespace(s, ref pos);
        var start = pos;
        var sawValueType = false;
        var sawClass = false;
        while (true)
        {
            if (TryKeyword(s, ref pos, "valuetype"))
            {
                sawValueType = true;
            }
            else if (TryKeyword(s, ref pos, "class"))
            {
                sawClass = true;
            }
            else
            {
                break;
            }

            SkipWhitespace(s, ref pos);
        }

        TypeSyntax syntax;
        if (TryKeyword(s, ref pos, "method"))
        {
            syntax = ParseFunctionPointer(s, ref pos, start, end);
        }
        else if (pos < end && s[pos] == '!')
        {
            pos++;
            var isMethod = pos < end && s[pos] == '!';
            if (isMethod)
            {
                pos++;
            }

            var referenceStart = pos;
            while (pos < end && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_'))
            {
                pos++;
            }

            if (referenceStart == pos)
            {
                throw new ReplException("expected an index or name after '!'");
            }

            syntax = new TypeSyntax
            {
                Kind = isMethod ? TypeSyntaxKind.MethodParameter : TypeSyntaxKind.TypeParameter,
                Start = start,
                End = pos,
                Reference = s[referenceStart..pos],
                ValueTypeKeyword = sawValueType,
                ClassKeyword = sawClass,
            };
        }
        else
        {
            string? asm = null;
            var hintStart = -1;
            var hintEnd = -1;
            if (pos < end && s[pos] == '[')
            {
                var close = s.IndexOf(']', pos);
                if (close < 0 || close >= end)
                {
                    throw new ReplException("unterminated '[' in type");
                }

                hintStart = pos;
                asm = s.Substring(pos + 1, close - pos - 1).Trim();
                pos = close + 1;
                hintEnd = pos;
            }

            // A name is a run of name characters, in which a quoted segment stands for a name ILAsm
            // could not read bare: Outer/'<>c' is the nested type <>c of Outer.
            var nameStart = pos;
            var nameBuilder = new StringBuilder();
            while (pos < end)
            {
                if (s[pos] == '\'')
                {
                    var closeQuote = EndOfQuoted(s, pos);
                    nameBuilder.Append(DecodeQuoted(s[(pos + 1)..closeQuote]));
                    pos = closeQuote + 1;
                }
                else if (IsNameChar(s[pos]))
                {
                    nameBuilder.Append(s[pos]);
                    pos++;
                }
                else
                {
                    break;
                }
            }

            if (nameStart == pos)
            {
                throw new ReplException(pos < end ? $"expected a type at '{s[pos..end]}'" : "expected a type");
            }

            var name = nameBuilder.ToString();

            // A multi-word keyword, native int or unsigned int8, is read as one primitive.
            if (asm is null && CilPrimitives.StartsMultiWord(name))
            {
                var lookahead = pos;
                var words = name;
                while (true)
                {
                    var wordStart = lookahead;
                    SkipWhitespace(s, ref wordStart);
                    var wordEnd = wordStart;
                    while (wordEnd < end && char.IsLetterOrDigit(s[wordEnd]))
                    {
                        wordEnd++;
                    }

                    if (wordEnd == wordStart)
                    {
                        break;
                    }

                    var candidate = words + " " + s[wordStart..wordEnd];
                    if (CilPrimitives.TryCanonical(candidate, out _))
                    {
                        words = candidate;
                        lookahead = wordEnd;
                        pos = wordEnd;
                        name = candidate;
                        break;
                    }

                    if (candidate is "native unsigned")
                    {
                        words = candidate;
                        lookahead = wordEnd;
                        continue;
                    }

                    break;
                }
            }

            var nameEnd = pos;
            var argumentsStart = pos;
            SkipWhitespace(s, ref argumentsStart);
            if (argumentsStart < end && s[argumentsStart] == '<')
            {
                pos = argumentsStart;
            }

            if (asm is null && CilPrimitives.TryCanonical(name, out var keyword) && !(pos < end && s[pos] == '<'))
            {
                syntax = new TypeSyntax
                {
                    Kind = TypeSyntaxKind.Primitive,
                    Start = start,
                    End = pos,
                    Keyword = keyword,
                    Name = name,
                    NameStart = nameStart,
                    NameEnd = nameEnd,
                    ValueTypeKeyword = sawValueType,
                    ClassKeyword = sawClass,
                };
            }
            else
            {
                var arguments = new List<TypeSyntax>();
                if (pos < end && s[pos] == '<')
                {
                    pos++;
                    while (true)
                    {
                        arguments.Add(ParseTypeAt(s, ref pos, end));
                        SkipWhitespace(s, ref pos);
                        if (pos < end && s[pos] == ',')
                        {
                            pos++;
                            continue;
                        }

                        if (pos < end && s[pos] == '>')
                        {
                            pos++;
                            break;
                        }

                        throw new ReplException("expected ',' or '>' in generic type arguments");
                    }
                }

                syntax = new TypeSyntax
                {
                    Kind = TypeSyntaxKind.Named,
                    Start = start,
                    End = pos,
                    AssemblyHint = asm,
                    HintStart = hintStart,
                    HintEnd = hintEnd,
                    Name = name,
                    NameStart = nameStart,
                    NameEnd = nameEnd,
                    Arguments = arguments,
                    ValueTypeKeyword = sawValueType,
                    ClassKeyword = sawClass,
                };
            }
        }

        // Suffixes: [] [,] [0...] & * pinned modreq(T) modopt(T)
        while (pos < end)
        {
            var suffixStart = pos;
            SkipWhitespace(s, ref pos);
            if (pos >= end || s[pos] is not ('[' or '&' or '*'))
            {
                pos = suffixStart;
            }

            if (s[pos] == '[' && IsArraySuffix(s, pos, end, out var close))
            {
                var inner = s.Substring(pos + 1, close - pos - 1).Replace(" ", "", StringComparison.Ordinal);
                var rank = inner.Length == 0 ? 1 : inner.Count(c => c == ',') + 1;
                pos = close + 1;
                syntax = new TypeSyntax
                {
                    Kind = TypeSyntaxKind.Array,
                    Start = start,
                    End = pos,
                    Element = syntax,
                    Rank = rank,
                    IsVector = inner.Length == 0,
                    Shape = inner
                };
                continue;
            }

            if (s[pos] == '&')
            {
                pos++;
                syntax = new TypeSyntax { Kind = TypeSyntaxKind.ByRef, Start = start, End = pos, Element = syntax };
                continue;
            }

            if (s[pos] == '*')
            {
                pos++;
                syntax = new TypeSyntax { Kind = TypeSyntaxKind.Pointer, Start = start, End = pos, Element = syntax };
                continue;
            }

            var before = pos;
            SkipWhitespace(s, ref pos);
            if (pos < end && TryKeyword(s, ref pos, "pinned"))
            {
                syntax = new TypeSyntax { Kind = TypeSyntaxKind.Pinned, Start = start, End = pos, Element = syntax };
            }
            else if (pos < end && (TryKeyword(s, ref pos, "modreq") || TryKeyword(s, ref pos, "modopt")))
            {
                var required = s[(pos - 6)..pos] == "modreq";
                SkipWhitespace(s, ref pos);
                if (pos >= end || s[pos] != '(')
                {
                    throw new ReplException("expected '(' after modreq/modopt");
                }

                pos++;
                var modifier = ParseTypeAt(s, ref pos, end);
                SkipWhitespace(s, ref pos);
                if (pos >= end || s[pos] != ')')
                {
                    throw new ReplException("expected ')' after modreq/modopt type");
                }

                pos++;
                syntax = new TypeSyntax
                {
                    Kind = TypeSyntaxKind.Modified,
                    Start = start,
                    End = pos,
                    Element = syntax,
                    Modifier = modifier,
                    IsRequired = required
                };
            }
            else
            {
                pos = before;
                break;
            }
        }

        return syntax;
    }

    private static bool IsArraySuffix(string s, int open, int end, out int close)
    {
        close = s.IndexOf(']', open);
        if (close < 0 || close >= end)
        {
            return false;
        }

        for (var i = open + 1; i < close; i++)
        {
            if (!(char.IsDigit(s[i]) || char.IsWhiteSpace(s[i]) || s[i] is ',' or '.' or '-' or '+'))
            {
                return false;
            }
        }

        return true;
    }

    private static TypeSyntax ParseFunctionPointer(string s, ref int pos, int start, int end)
    {
        // method [callconv] RetType *(Params)
        SkipWhitespace(s, ref pos);
        var words = new List<string>();
        while (true)
        {
            var wordStart = pos;
            if (TryKeyword(s, ref pos, "instance") || TryKeyword(s, ref pos, "explicit") || TryKeyword(s, ref pos, "unmanaged")
                || TryKeyword(s, ref pos, "cdecl") || TryKeyword(s, ref pos, "stdcall") || TryKeyword(s, ref pos, "thiscall")
                || TryKeyword(s, ref pos, "fastcall") || TryKeyword(s, ref pos, "vararg") || TryKeyword(s, ref pos, "default"))
            {
                words.Add(s[wordStart..pos]);
                SkipWhitespace(s, ref pos);
                continue;
            }

            break;
        }

        // The return type ends at the '*' that is followed by the parameter list.
        var star = -1;
        for (var i = pos; i < end; i++)
        {
            if (s[i] != '*')
            {
                continue;
            }

            var j = i + 1;
            SkipWhitespace(s, ref j);
            if (j < end && s[j] == '(')
            {
                star = i;
                break;
            }
        }

        if (star < 0)
        {
            throw new ReplException("expected '*(' in function pointer type (method RetType *(Params))");
        }

        var returnType = ParseTypeIn(s, pos, star);
        pos = star + 1;
        SkipWhitespace(s, ref pos);
        var close = FindMatchingParen(s, pos);
        var (parameters, sentinel) = ParseParameterList(s, pos + 1, close);
        pos = close + 1;
        return new TypeSyntax
        {
            Kind = TypeSyntaxKind.FunctionPointer,
            Start = start,
            End = pos,
            FunctionPointer = new SignatureSyntax(words, returnType, parameters, sentinel),
        };
    }

    /// <summary>
    /// Parses a comma-separated list of types between two positions, with at most one <c>...</c>.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="start">The index after the opening parenthesis.</param>
    /// <param name="end">The index of the closing parenthesis.</param>
    /// <returns>The types and the index the sentinel was written before, if any.</returns>
    /// <exception cref="ReplException">An item is not a type, or two sentinels were written.</exception>
    public static (IReadOnlyList<TypeSyntax> Parameters, int? SentinelIndex) ParseParameterList(string s, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(s);
        var parameters = new List<TypeSyntax>();
        int? sentinel = null;
        foreach (var (itemStart, itemEnd) in SplitTopLevelRanges(s, start, end))
        {
            if (s[itemStart..itemEnd].Trim() == "...")
            {
                if (sentinel is not null)
                {
                    throw new ReplException("only one '...' is allowed in a parameter list");
                }

                sentinel = parameters.Count;
                continue;
            }

            parameters.Add(ParseTypeIn(s, itemStart, itemEnd));
        }

        return (parameters, sentinel);
    }

    /// <summary>
    /// Parses a qualified or session method reference.
    /// </summary>
    /// <remarks>
    /// Parses a method reference: <c>[instance] [vararg] [RetType] Declaring::Name[&lt;Args&gt;][(Params)]</c>,
    /// or the session form <c>[RetType] Name(Params)</c> when there is no <c>::</c>.
    /// </remarks>
    /// <param name="text">The reference text, comments removed.</param>
    /// <returns>The syntax.</returns>
    /// <exception cref="ReplException">The reference is malformed.</exception>
    public static MemberSyntax ParseMethodReference(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ParseMethodReferenceIn(text, 0, text.Length);
    }

    /// <summary>
    /// Parses a method reference within a source range while retaining its original positions.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="start">The index the reference starts at.</param>
    /// <param name="end">The index the reference ends by.</param>
    /// <returns>The syntax.</returns>
    /// <exception cref="ReplException">The reference is malformed.</exception>
    public static MemberSyntax ParseMethodReferenceIn(string s, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(s);
        var pos = start;
        var explicitInstance = false;
        var isVarArg = false;
        SkipWhitespace(s, ref pos);
        start = pos;
        while (true)
        {
            SkipWhitespace(s, ref pos);
            if (TryKeyword(s, ref pos, "instance"))
            {
                explicitInstance = true;
            }
            else if (TryKeyword(s, ref pos, "vararg"))
            {
                isVarArg = true;
            }
            else if (TryKeyword(s, ref pos, "default") || TryKeyword(s, ref pos, "explicit"))
            {
                // Accepted for completeness.
            }
            else
            {
                break;
            }
        }

        var typesStart = pos;
        var endOfText = end;
        while (endOfText > pos && char.IsWhiteSpace(s[endOfText - 1]))
        {
            endOfText--;
        }

        var separator = FindMemberSeparator(s, pos, endOfText);
        if (separator < 0)
        {
            return ParseSessionReference(s, pos, endOfText, start, explicitInstance, isVarArg);
        }

        // Name, method generic arguments, and parameters come from the right-hand side. A quoted
        // name, '<Main>b__0_0', is read as one name however it is spelled inside the quotes.
        var rest = separator + 2;
        SkipWhitespace(s, ref rest);
        var (name, quoted, nameEnd) = ReadMemberName(s, rest, endOfText);
        var afterName = nameEnd;
        SkipWhitespace(s, ref afterName);
        var genericStart = -1;
        var genericEnd = -1;
        var afterGeneric = afterName;
        if (afterName < endOfText && s[afterName] == '<')
        {
            genericStart = afterName;
            var closeAngle = FindMatchingAngle(s, afterName, endOfText);
            genericEnd = closeAngle + 1;
            afterGeneric = closeAngle + 1;
            SkipWhitespace(s, ref afterGeneric);
        }

        var parametersStart = -1;
        var parametersEnd = -1;
        var paren = afterGeneric < endOfText && s[afterGeneric] == '(' ? afterGeneric : s.IndexOf('(', afterGeneric);
        if (paren >= endOfText)
        {
            paren = -1;
        }

        if (paren >= 0)
        {
            if (s[afterGeneric..paren].Trim().Length > 0)
            {
                throw new ReplException($"unexpected '{s[afterGeneric..paren].Trim()}' before parameter list");
            }

            var close = FindMatchingParen(s, paren);
            parametersStart = paren;
            parametersEnd = close + 1;
            if (s[(close + 1)..endOfText].Trim().Length > 0)
            {
                throw new ReplException($"unexpected '{s[(close + 1)..endOfText].Trim()}' after parameter list");
            }
        }
        else if (s[afterGeneric..endOfText].Trim().Length > 0)
        {
            throw new ReplException($"unexpected '{s[afterGeneric..endOfText].Trim()}' in method reference");
        }

        if (name.Length == 0)
        {
            throw new ReplException("missing method name");
        }

        // <[N]> names a generic method definition of arity N without instantiating it, as ILAsm
        // spells a token for one.
        int? genericArity = null;
        List<TypeSyntax>? genericArguments = null;
        if (genericStart >= 0)
        {
            var genericText = s[(genericStart + 1)..(genericEnd - 1)];
            if (ArityMarker().Match(genericText) is { Success: true } arityMatch)
            {
                var arityValue = int.Parse(arityMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                if (arityValue < 1)
                {
                    throw new ReplException($"expected a generic arity such as <[1]>, got '<{genericText}>'");
                }

                genericArity = arityValue;
            }
        }

        var (returnType, declaring) = ParseLeft(s, typesStart, separator);
        if (genericStart >= 0 && genericArity is null)
        {
            genericArguments = [];
            foreach (var (itemStart, itemEnd) in SplitTopLevelRanges(s, genericStart + 1, genericEnd - 1))
            {
                genericArguments.Add(ParseTypeIn(s, itemStart, itemEnd));
            }
        }

        IReadOnlyList<TypeSyntax>? parameters = null;
        int? sentinel = null;
        if (parametersStart >= 0)
        {
            (parameters, sentinel) = ParseParameterList(s, parametersStart + 1, parametersEnd - 1);
        }

        return new MemberSyntax
        {
            Start = start,
            End = endOfText,
            ExplicitInstance = explicitInstance,
            IsVarArg = isVarArg || sentinel is not null,
            TypesStart = typesStart,
            ReturnType = returnType,
            DeclaringType = declaring,
            SeparatorIndex = separator,
            Name = name,
            NameStart = rest,
            NameEnd = nameEnd,
            NameQuoted = quoted,
            GenericArguments = genericArguments,
            GenericArity = genericArity,
            GenericStart = genericStart,
            GenericEnd = genericEnd,
            Parameters = parameters,
            SentinelIndex = sentinel,
            ParametersStart = parametersStart,
            ParametersEnd = parametersEnd,
        };
    }

    /// <summary>
    /// Parses a field reference, <c>[FieldType] Declaring::Name</c>.
    /// </summary>
    /// <param name="text">The reference text, comments removed.</param>
    /// <returns>The syntax, with no parameters or generic arguments.</returns>
    /// <exception cref="ReplException">The reference is malformed.</exception>
    public static MemberSyntax ParseFieldReference(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ParseFieldReferenceIn(text, 0, text.Length);
    }

    /// <summary>
    /// Parses the field reference between two positions of a longer text.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="start">The index the reference starts at.</param>
    /// <param name="end">The index the reference ends by.</param>
    /// <returns>The syntax.</returns>
    /// <exception cref="ReplException">The reference is malformed.</exception>
    public static MemberSyntax ParseFieldReferenceIn(string s, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(s);
        var pos = start;
        SkipWhitespace(s, ref pos);
        var endOfText = end;
        while (endOfText > pos && char.IsWhiteSpace(s[endOfText - 1]))
        {
            endOfText--;
        }

        var separator = FindMemberSeparator(s, pos, endOfText);
        if (separator < 0)
        {
            throw new ReplException("expected 'Type::field' in field reference");
        }

        var (fieldType, declaring) = ParseLeft(s, pos, separator);
        var nameStart = separator + 2;
        SkipWhitespace(s, ref nameStart);
        var rawName = s[nameStart..endOfText];
        var quoted = rawName.Length > 2 && rawName.StartsWith('\'') && rawName.EndsWith('\'');
        var name = quoted ? DecodeQuoted(rawName[1..^1]) : rawName;
        if (name.Length == 0)
        {
            throw new ReplException("missing field name");
        }

        return new MemberSyntax
        {
            Start = pos,
            End = endOfText,
            TypesStart = pos,
            ReturnType = fieldType,
            DeclaringType = declaring,
            SeparatorIndex = separator,
            Name = name,
            NameStart = nameStart,
            NameEnd = endOfText,
            NameQuoted = quoted,
        };
    }

    private static MemberSyntax ParseSessionReference(string s, int pos, int endOfText, int start, bool explicitInstance, bool isVarArg)
    {
        // "[ret] Name(params)" with no "::" names a method defined with .method. The return type
        // is optional, as it is for a framework method, and may contain parentheses of its own
        // (modopt, a function pointer), so it is parsed as a type when the text before the first
        // '(' has room for one.
        var firstParen = s.IndexOf('(', pos);
        if (firstParen >= endOfText)
        {
            firstParen = -1;
        }

        var head = (firstParen < 0 ? s[pos..endOfText] : s[pos..firstParen]).Trim();
        TypeSyntax? returnType = null;
        if (head.Any(char.IsWhiteSpace))
        {
            returnType = ParseTypeAt(s, ref pos, endOfText);
            SkipWhitespace(s, ref pos);
        }

        var nameEnd = pos;
        while (nameEnd < endOfText && s[nameEnd] != '(' && !char.IsWhiteSpace(s[nameEnd]))
        {
            nameEnd++;
        }

        var rawName = s[pos..nameEnd];
        var quoted = rawName.Length > 2 && rawName.StartsWith('\'') && rawName.EndsWith('\'');
        var name = quoted ? DecodeQuoted(rawName[1..^1]) : rawName;
        var afterName = nameEnd;
        SkipWhitespace(s, ref afterName);
        var paren = afterName < endOfText && s[afterName] == '(' ? afterName : -1;
        if (paren < 0 && afterName < endOfText)
        {
            throw new ReplException($"unexpected '{s[afterName..endOfText]}' in method reference");
        }

        if (name.Contains('<', StringComparison.Ordinal))
        {
            throw new ReplException("session methods are not generic");
        }

        if (!InstructionParser.IsIdentifier(name))
        {
            throw new ReplException("expected 'Type::Method(...)' in method reference (or a session method name defined with .method)");
        }

        IReadOnlyList<TypeSyntax>? parameters = null;
        int? sentinel = null;
        var parametersEnd = -1;
        if (paren >= 0)
        {
            var close = FindMatchingParen(s, paren);
            var trailing = s[(close + 1)..endOfText].Trim();
            if (trailing.Length > 0)
            {
                throw new ReplException($"unexpected '{trailing}' after parameter list");
            }

            (parameters, sentinel) = ParseParameterList(s, paren + 1, close);
            parametersEnd = close + 1;
        }

        return new MemberSyntax
        {
            Start = start,
            End = endOfText,
            ExplicitInstance = explicitInstance,
            IsVarArg = isVarArg || sentinel is not null,
            TypesStart = start,
            ReturnType = returnType,
            DeclaringType = null,
            Name = name,
            NameStart = pos,
            NameEnd = nameEnd,
            NameQuoted = quoted,
            Parameters = parameters,
            SentinelIndex = sentinel,
            ParametersStart = paren,
            ParametersEnd = parametersEnd,
        };
    }

    /// <summary>
    /// Reads the declaring type and optional return type preceding <c>::</c>.
    /// </summary>
    /// <remarks>
    /// Reads the one or two types before a <c>::</c>: the declaring type alone, or the return
    /// type and then the declaring type.
    /// </remarks>
    private static (TypeSyntax? ReturnType, TypeSyntax Declaring) ParseLeft(string s, int start, int end)
    {
        var pos = start;
        SkipWhitespace(s, ref pos);
        var first = ParseTypeAt(s, ref pos, end);
        SkipWhitespace(s, ref pos);
        if (pos >= end)
        {
            return (null, first);
        }

        var second = ParseTypeAt(s, ref pos, end);
        SkipWhitespace(s, ref pos);
        if (pos < end)
        {
            throw new ReplException($"unexpected '{s[pos..end]}' in member reference");
        }

        return (first, second);
    }

    /// <summary>
    /// Parses a managed or unmanaged <c>calli</c> signature.
    /// </summary>
    /// <remarks>
    /// Parses a <c>calli</c> signature: <c>[instance] [vararg] RetType(Params)</c> for managed
    /// pointers and <c>unmanaged [cdecl|stdcall|thiscall|fastcall] RetType(Params)</c> for native ones.
    /// </remarks>
    /// <param name="text">The signature text.</param>
    /// <returns>The syntax.</returns>
    /// <exception cref="ReplException">The signature is malformed.</exception>
    public static SignatureSyntax ParseCalliSignature(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ParseCalliSignatureIn(text, 0, text.Length);
    }

    /// <summary>
    /// Parses the <c>calli</c> signature between two positions of a longer text.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="start">The index the signature starts at.</param>
    /// <param name="end">The index the signature ends by.</param>
    /// <returns>The syntax.</returns>
    /// <exception cref="ReplException">The signature is malformed.</exception>
    public static SignatureSyntax ParseCalliSignatureIn(string s, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(s);
        var pos = start;
        SkipWhitespace(s, ref pos);
        while (end > pos && char.IsWhiteSpace(s[end - 1]))
        {
            end--;
        }

        if (pos >= end)
        {
            throw new ReplException("calli needs a signature, e.g. calli int32(int32, int32)");
        }

        var words = new List<string>();
        while (true)
        {
            SkipWhitespace(s, ref pos);
            var wordStart = pos;
            if (TryKeyword(s, ref pos, "instance") || TryKeyword(s, ref pos, "explicit") || TryKeyword(s, ref pos, "vararg")
                || TryKeyword(s, ref pos, "unmanaged") || TryKeyword(s, ref pos, "cdecl") || TryKeyword(s, ref pos, "stdcall")
                || TryKeyword(s, ref pos, "thiscall") || TryKeyword(s, ref pos, "fastcall") || TryKeyword(s, ref pos, "default"))
            {
                words.Add(s[wordStart..pos]);
                continue;
            }

            break;
        }

        var returnType = ParseTypeAt(s, ref pos, end);
        SkipWhitespace(s, ref pos);
        if (pos >= end || s[pos] != '(')
        {
            throw new ReplException("calli needs a parameter list in parentheses");
        }

        var open = pos;
        var close = FindMatchingParen(s, open);
        var parameters = new List<TypeSyntax>();
        int? sentinel = null;
        foreach (var (itemStart, itemEnd) in SplitTopLevelRanges(s, open + 1, close))
        {
            if (s[itemStart..itemEnd].Trim() == "...")
            {
                if (sentinel is not null)
                {
                    throw new ReplException("only one '...' is allowed in a signature");
                }

                sentinel = parameters.Count;
                continue;
            }

            parameters.Add(ParseTypeIn(s, itemStart, itemEnd));
        }

        if (close + 1 < end && s[(close + 1)..end].Trim().Length > 0)
        {
            throw new ReplException($"unexpected '{s[(close + 1)..end].Trim()}' after calli signature");
        }

        return new SignatureSyntax(words, returnType, parameters, sentinel);
    }

    /// <summary>
    /// Parses an opcode and its complete operand without labels or comments.
    /// </summary>
    /// <remarks>
    /// Parses an instruction, opcode plus operand, with no labels or comments: the opcode decides
    /// what shape the operand takes, and a type, member, or signature operand is read in full.
    /// </remarks>
    /// <param name="text">The instruction text.</param>
    /// <returns>The syntax.</returns>
    /// <exception cref="ReplException">The opcode is unknown or the operand is malformed.</exception>
    public static InstructionSyntax ParseInstruction(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        var mnemonicEnd = 0;
        while (mnemonicEnd < text.Length && !char.IsWhiteSpace(text[mnemonicEnd]))
        {
            mnemonicEnd++;
        }

        var mnemonic = text[..mnemonicEnd];
        var operandStart = mnemonicEnd;
        SkipWhitespace(text, ref operandStart);
        var operandEnd = text.Length;
        var operandText = text[operandStart..operandEnd];

        if (mnemonic == "no.")
        {
            throw new ReplException("the 'no.' prefix has no ILGenerator representation and cannot be emitted");
        }

        if (!OpcodeTable.TryGet(mnemonic, out var op) || op.Name is null || OpcodeTable.IsReserved(op.Name))
        {
            var suggestion = InstructionParser.SuggestOpcode(mnemonic);
            throw new ReplException($"unknown opcode '{mnemonic}'" + (suggestion is null ? "" : $" (did you mean '{suggestion}'?)"));
        }

        var opName = op.Name;
        OperandSyntax operand;
        switch (op.OperandType)
        {
            case System.Reflection.Emit.OperandType.InlineNone:
                operand = Plain(OperandSyntaxKind.None);
                break;
            case System.Reflection.Emit.OperandType.ShortInlineI:
            case System.Reflection.Emit.OperandType.InlineI:
            case System.Reflection.Emit.OperandType.InlineI8:
                operand = Plain(OperandSyntaxKind.Integer);
                break;
            case System.Reflection.Emit.OperandType.ShortInlineR:
            case System.Reflection.Emit.OperandType.InlineR:
                operand = Plain(OperandSyntaxKind.Float);
                break;
            case System.Reflection.Emit.OperandType.InlineString:
                operand = Plain(OperandSyntaxKind.String);
                break;
            case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
            case System.Reflection.Emit.OperandType.InlineBrTarget:
                if (!InstructionParser.IsIdentifier(operandText))
                {
                    throw new ReplException($"'{opName}' needs a label name, e.g. {opName} LOOP");
                }

                operand = Plain(OperandSyntaxKind.Label);
                break;
            case System.Reflection.Emit.OperandType.InlineSwitch:
            {
                var inner = operandText;
                if (inner.StartsWith('(') && inner.EndsWith(')'))
                {
                    inner = inner[1..^1];
                }

                var labels = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (labels.Length == 0 || labels.Any(l => !InstructionParser.IsIdentifier(l)))
                {
                    throw new ReplException("switch needs a list of labels: switch (A, B, C)");
                }

                operand = Plain(OperandSyntaxKind.Labels) with { Labels = labels };
                break;
            }

            case System.Reflection.Emit.OperandType.ShortInlineVar:
            case System.Reflection.Emit.OperandType.InlineVar:
                operand = Plain(OperandSyntaxKind.Variable) with
                {
                    IsArgument = opName.StartsWith("ldarg", StringComparison.Ordinal)
                    || opName.StartsWith("starg", StringComparison.Ordinal)
                };
                break;
            case System.Reflection.Emit.OperandType.InlineType:
                if (operandText.Length == 0)
                {
                    throw new ReplException($"'{opName}' needs a type operand");
                }

                operand = Plain(OperandSyntaxKind.Type) with { Type = ParseTypeIn(text, operandStart, operandEnd) };
                break;
            case System.Reflection.Emit.OperandType.InlineMethod:
                if (operandText.Length == 0)
                {
                    throw new ReplException($"'{opName}' needs a method reference, e.g. {opName} void Console::WriteLine(string)");
                }

                operand = Plain(OperandSyntaxKind.Member) with { Member = ParseMethodReferenceIn(text, operandStart, operandEnd) };
                break;
            case System.Reflection.Emit.OperandType.InlineField:
                if (operandText.Length == 0)
                {
                    throw new ReplException($"'{opName}' needs a field reference, e.g. {opName} string String::Empty");
                }

                operand = Plain(OperandSyntaxKind.Field) with { Member = ParseFieldReferenceIn(text, operandStart, operandEnd) };
                break;
            case System.Reflection.Emit.OperandType.InlineTok:
                if (operandText.StartsWith("method ", StringComparison.Ordinal))
                {
                    operand = Plain(OperandSyntaxKind.Token) with
                    {
                        IsMethodToken = true,
                        Member = ParseMethodReferenceIn(text,
                        operandStart + 7, operandEnd)
                    };
                }
                else if (operandText.StartsWith("field ", StringComparison.Ordinal))
                {
                    operand = Plain(OperandSyntaxKind.Token) with
                    {
                        IsFieldToken = true,
                        Member = ParseFieldReferenceIn(text, operandStart
                        + 6, operandEnd)
                    };
                }
                else
                {
                    if (operandText.Length == 0)
                    {
                        throw new ReplException("expected a type");
                    }

                    operand = Plain(OperandSyntaxKind.Token) with { Type = ParseTypeIn(text, operandStart, operandEnd) };
                }

                break;
            case System.Reflection.Emit.OperandType.InlineSig:
                operand = Plain(OperandSyntaxKind.Signature) with { Signature = ParseCalliSignatureIn(text, operandStart, operandEnd) };
                break;
            default:
                throw new ReplException($"unsupported operand type {op.OperandType} for '{opName}'");
        }

        return new InstructionSyntax(op, text, mnemonicEnd, operand);

        OperandSyntax Plain(OperandSyntaxKind kind) => new() { Kind = kind, Text = operandText, Start = operandStart, End = operandEnd };
    }

    /// <summary>
    /// Finds the depth-0 <c>::</c> between two positions, skipping quoted names and nested lists.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="start">The index to start at.</param>
    /// <param name="end">The index to stop at.</param>
    /// <returns>The index of the first colon, or -1.</returns>
    public static int FindMemberSeparator(string s, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(s);
        var depth = 0;
        for (var i = start; i + 1 < end; i++)
        {
            if (s[i] == '\'')
            {
                i = EndOfQuoted(s, i);
                continue;
            }

            switch (s[i])
            {
                case '<':
                case '(':
                case '[':
                    depth++;
                    break;
                case '>':
                case ')':
                case ']':
                    depth--;
                    break;
                case ':' when depth == 0 && s[i + 1] == ':':
                    return i;
                default:
                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads a quoted or unquoted member name at the requested position.
    /// </summary>
    /// <remarks>
    /// Reads the member name at a position: a quoted name up to its closing quote, otherwise
    /// everything before a generic argument list, a parameter list, or whitespace.
    /// </remarks>
    private static (string Name, bool Quoted, int End) ReadMemberName(string s, int start, int end)
    {
        if (start < end && s[start] == '\'')
        {
            var close = EndOfQuoted(s, start);
            return (DecodeQuoted(s[(start + 1)..close]), true, close + 1);
        }

        var pos = start;
        while (pos < end && s[pos] != '<' && s[pos] != '(' && !char.IsWhiteSpace(s[pos]))
        {
            pos++;
        }

        return (s[start..pos], false, pos);
    }

    private static int FindMatchingAngle(string s, int open, int end)
    {
        var depth = 0;
        for (var i = open; i < end; i++)
        {
            if (s[i] == '\'')
            {
                i = EndOfQuoted(s, i);
            }
            else if (s[i] == '<')
            {
                depth++;
            }
            else if (s[i] == '>' && --depth == 0)
            {
                return i;
            }
        }

        throw new ReplException("unbalanced '<' in method name");
    }

    /// <summary>
    /// Recognizes a generic arity token such as <c>[1]</c> separately from an assembly-qualified type.
    /// </summary>
    /// <remarks>
    /// The arity form of a generic argument list: a bracketed integer alone, <c>[1]</c>, which is
    /// not a type, unlike <c>[System.Runtime]System.String[]</c>.
    /// </remarks>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\[\s*([0-9]+)\s*\]\s*$")]
    private static partial System.Text.RegularExpressions.Regex ArityMarker();

    /// <summary>
    /// Finds the quote that closes a quoted name, skipping escaped characters inside it.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="open">The index of the opening quote.</param>
    /// <returns>The index of the closing quote.</returns>
    /// <exception cref="ReplException">The quote is never closed.</exception>
    public static int EndOfQuoted(string s, int open)
    {
        ArgumentNullException.ThrowIfNull(s);
        for (var i = open + 1; i < s.Length; i++)
        {
            if (s[i] == '\\')
            {
                i++;
            }
            else if (s[i] == '\'')
            {
                return i;
            }
        }

        throw new ReplException("unterminated quote in name");
    }

    /// <summary>
    /// Decodes the inside of a quoted name: <c>\\\\</c> is a backslash and <c>\\'</c> a quote, as ILAsm reads them.
    /// </summary>
    /// <param name="inner">The text between the quotes.</param>
    /// <returns>The name.</returns>
    public static string DecodeQuoted(string inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.Contains('\\'))
        {
            return inner;
        }

        var sb = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                i++;
            }

            sb.Append(inner[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Splits a comma-separated list while respecting nested brackets and quotes.
    /// </summary>
    /// <param name="s">The list text.</param>
    /// <returns>The trimmed items.</returns>
    public static List<string> SplitTopLevel(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return [.. SplitTopLevelRanges(s, 0, s.Length).Select(r => s[r.Start..r.End].Trim())];
    }

    /// <summary>
    /// Splits a source range into comma-separated items while respecting nested brackets and quotes.
    /// </summary>
    /// <remarks>
    /// Splits a comma-separated list between two positions into the ranges of its items, respecting
    /// nested brackets and quotes. An item's range keeps its surrounding whitespace; an empty
    /// trailing item is dropped, as <see cref="SplitTopLevel"/> drops it.
    /// </remarks>
    /// <param name="s">The text.</param>
    /// <param name="start">The index to start at.</param>
    /// <param name="end">The index to stop at.</param>
    /// <returns>The item ranges.</returns>
    public static List<(int Start, int End)> SplitTopLevelRanges(string s, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(s);
        var parts = new List<(int Start, int End)>();
        int depth = 0, itemStart = start;
        for (var i = start; i < end; i++)
        {
            if (s[i] == '\'')
            {
                i = EndOfQuoted(s, i);
                continue;
            }

            switch (s[i])
            {
                case '<':
                case '[':
                case '(':
                    depth++;
                    break;
                case '>':
                case ']':
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add((itemStart, i));
                    itemStart = i + 1;
                    break;
                default:
                    break;
            }
        }

        if (s[itemStart..end].Trim().Length > 0)
        {
            parts.Add((itemStart, end));
        }

        return parts;
    }

    /// <summary>
    /// Skips whitespace starting at <paramref name="pos"/>.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="pos">The position to advance.</param>
    public static void SkipWhitespace(string s, ref int pos)
    {
        ArgumentNullException.ThrowIfNull(s);
        while (pos < s.Length && char.IsWhiteSpace(s[pos]))
        {
            pos++;
        }
    }

    /// <summary>
    /// Consumes <paramref name="keyword"/> at <paramref name="pos"/> when it is present as a whole word.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="pos">The position; advanced past the keyword on success.</param>
    /// <param name="keyword">The keyword to match.</param>
    /// <returns>True when the keyword was consumed.</returns>
    public static bool TryKeyword(string s, ref int pos, string keyword)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(keyword);
        if (string.CompareOrdinal(s, pos, keyword, 0, keyword.Length) == 0
            && (pos + keyword.Length == s.Length || !IsNameChar(s[pos + keyword.Length])))
        {
            pos += keyword.Length;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the index of the parenthesis that closes the one at <paramref name="open"/>.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="open">The index of the opening parenthesis.</param>
    /// <returns>The index of the matching closing parenthesis.</returns>
    /// <exception cref="ReplException">The parentheses are unbalanced.</exception>
    public static int FindMatchingParen(string s, int open)
    {
        ArgumentNullException.ThrowIfNull(s);
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '\'')
            {
                i = EndOfQuoted(s, i);
            }
            else if (s[i] == '(')
            {
                depth++;
            }
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        throw new ReplException("unbalanced parentheses");
    }

    /// <summary>
    /// True when <paramref name="c"/> can appear inside an IL type or member name.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <returns>True for letters, digits, and the punctuation IL names allow.</returns>
    public static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '.' or '_' or '`' or '/' or '$' or '@' or '+';
}
