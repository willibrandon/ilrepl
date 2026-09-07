using System.Globalization;

namespace IlRepl.Engine;

/// <summary>
/// Parses ILAsm type syntax into <see cref="Type"/> instances: primitives, <c>[assembly]Namespace.Type</c>,
/// nested <c>Outer/Inner</c>, generic instantiations, <c>!N</c> and <c>!!N</c> parameters, arrays, byrefs,
/// pointers, <c>pinned</c>, <c>modreq</c>/<c>modopt</c>, and <c>method</c> function pointer signatures.
/// </summary>
public static class TypeParser
{
    private static readonly Dictionary<string, Type> Primitives = new(StringComparer.Ordinal)
    {
        ["void"] = typeof(void), ["bool"] = typeof(bool), ["char"] = typeof(char),
        ["int8"] = typeof(sbyte), ["uint8"] = typeof(byte),
        ["int16"] = typeof(short), ["uint16"] = typeof(ushort),
        ["int32"] = typeof(int), ["uint32"] = typeof(uint),
        ["int64"] = typeof(long), ["uint64"] = typeof(ulong),
        ["float32"] = typeof(float), ["float64"] = typeof(double),
        ["string"] = typeof(string), ["object"] = typeof(object),
        ["nativeint"] = typeof(nint), ["nativeuint"] = typeof(nuint),
        ["typedref"] = typeof(TypedReference),
        // C# spellings are accepted too; they are handy at a prompt.
        ["int"] = typeof(int), ["long"] = typeof(long), ["short"] = typeof(short), ["byte"] = typeof(byte),
        ["sbyte"] = typeof(sbyte), ["uint"] = typeof(uint), ["ulong"] = typeof(ulong), ["ushort"] = typeof(ushort),
        ["float"] = typeof(float), ["double"] = typeof(double), ["decimal"] = typeof(decimal), ["nint"] = typeof(nint),
        ["nuint"] = typeof(nuint),
    };

    /// <summary>
    /// Rewrites multi-word IL keywords into single tokens so the rest of the parser can treat them as names.
    /// </summary>
    /// <param name="text">The IL text.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text
            .Replace("native unsigned int", "nativeuint", StringComparison.Ordinal)
            .Replace("native uint", "nativeuint", StringComparison.Ordinal)
            .Replace("native int", "nativeint", StringComparison.Ordinal)
            .Replace("unsigned int8", "uint8", StringComparison.Ordinal)
            .Replace("unsigned int16", "uint16", StringComparison.Ordinal)
            .Replace("unsigned int32", "uint32", StringComparison.Ordinal)
            .Replace("unsigned int64", "uint64", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses a complete type expression.
    /// </summary>
    /// <param name="text">The type in IL syntax.</param>
    /// <param name="context">The parse context.</param>
    /// <returns>The parsed type.</returns>
    /// <exception cref="ReplException">The text is not a valid type or the type cannot be found.</exception>
    public static Type Parse(string text, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);
        var s = Normalize(text);
        var pos = 0;
        var t = ParseAt(s, ref pos, context, out _);
        SkipWhitespace(s, ref pos);
        if (pos != s.Length)
        {
            throw new ReplException($"unexpected '{s[pos..]}' after type");
        }

        return t;
    }

    /// <summary>
    /// Parses a type starting at <paramref name="pos"/> and advances past it.
    /// </summary>
    /// <param name="s">The normalized text.</param>
    /// <param name="pos">The position to start at; updated to the first character after the type.</param>
    /// <param name="context">The parse context.</param>
    /// <param name="pinned">Set to true when the type carried the <c>pinned</c> modifier.</param>
    /// <returns>The parsed type.</returns>
    /// <exception cref="ReplException">The text is not a valid type or the type cannot be found.</exception>
    public static Type ParseAt(string s, ref int pos, ParseContext context, out bool pinned) =>
        ParseAt(s, ref pos, context, out pinned, out _, out _);

    /// <summary>
    /// Parses a type starting at <paramref name="pos"/>, advances past it, and reports the custom
    /// modifiers written after it.
    /// </summary>
    /// <param name="s">The normalized text.</param>
    /// <param name="pos">The position to start at; updated to the first character after the type.</param>
    /// <param name="context">The parse context.</param>
    /// <param name="pinned">Set to true when the type carried the <c>pinned</c> modifier.</param>
    /// <param name="requiredModifiers">The <c>modreq</c> types, in order.</param>
    /// <param name="optionalModifiers">The <c>modopt</c> types, in order.</param>
    /// <returns>The parsed type.</returns>
    /// <exception cref="ReplException">The text is not a valid type or the type cannot be found.</exception>
    public static Type ParseAt(string s, ref int pos, ParseContext context, out bool pinned, out List<Type> requiredModifiers, out List<Type> optionalModifiers)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(context);
        pinned = false;
        requiredModifiers = [];
        optionalModifiers = [];
        SkipWhitespace(s, ref pos);
        var sawValueType = false;
        while (true)
        {
            if (TryKeyword(s, ref pos, "valuetype"))
            {
                sawValueType = true;
            }
            else if (!TryKeyword(s, ref pos, "class"))
            {
                break;
            }

            SkipWhitespace(s, ref pos);
        }

        Type t;
        if (TryKeyword(s, ref pos, "method"))
        {
            t = ParseFunctionPointer(s, ref pos, context);
        }
        else if (pos < s.Length && s[pos] == '!')
        {
            pos++;
            var isMethod = pos < s.Length && s[pos] == '!';
            if (isMethod)
            {
                pos++;
            }

            var start = pos;
            while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_'))
            {
                pos++;
            }

            if (start == pos)
            {
                throw new ReplException("expected an index or name after '!'");
            }

            t = context.Generics.Resolve(isMethod, s[start..pos]);
        }
        else
        {
            string? asm = null;
            if (pos < s.Length && s[pos] == '[')
            {
                var close = s.IndexOf(']', pos);
                if (close < 0)
                {
                    throw new ReplException("unterminated '[' in type");
                }

                asm = s.Substring(pos + 1, close - pos - 1).Trim();
                pos = close + 1;
            }

            // A name is a run of name characters, in which a quoted segment stands for a name ILAsm
            // could not read bare: Outer/'<>c' is the nested type <>c of Outer.
            var start = pos;
            var nameBuilder = new System.Text.StringBuilder();
            while (pos < s.Length)
            {
                if (s[pos] == '\'')
                {
                    var closeQuote = s.IndexOf('\'', pos + 1);
                    if (closeQuote < 0)
                    {
                        throw new ReplException("unterminated quote in type name");
                    }

                    nameBuilder.Append(s, pos + 1, closeQuote - pos - 1);
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

            if (start == pos)
            {
                throw new ReplException(pos < s.Length ? $"expected a type at '{s[pos..]}'" : "expected a type");
            }

            var name = nameBuilder.ToString();
            if (asm is null && Primitives.TryGetValue(name, out var primitiveType) && !(pos < s.Length && s[pos] == '<'))
            {
                t = primitiveType;
            }
            else if (asm is null or "ilrepl" && context.Types.TryResolve(name, pos < s.Length && s[pos] == '<', sawValueType, out var sessionType))
            {
                t = sessionType;
                if (pos < s.Length && s[pos] == '<')
                {
                    pos++;
                    var sessionArgs = new List<Type>();
                    while (true)
                    {
                        sessionArgs.Add(ParseAt(s, ref pos, context, out _));
                        SkipWhitespace(s, ref pos);
                        if (pos < s.Length && s[pos] == ',')
                        {
                            pos++;
                            continue;
                        }

                        if (pos < s.Length && s[pos] == '>')
                        {
                            pos++;
                            break;
                        }

                        throw new ReplException("expected ',' or '>' in generic type arguments");
                    }

                    var arity = t.GetGenericArguments().Length;
                    if (arity != sessionArgs.Count)
                    {
                        throw new ReplException($"'{TypeNameFormatter.Pretty(t)}' takes {arity} type argument(s), not {sessionArgs.Count}");
                    }

                    t = t.MakeGenericType([.. sessionArgs]);
                }
            }
            else if (asm == "ilrepl")
            {
                throw new ReplException($"no type '{name}' in the session (define one with .class)");
            }
            else if (pos < s.Length && s[pos] == '<')
            {
                pos++;
                var args = new List<Type>();
                while (true)
                {
                    args.Add(ParseAt(s, ref pos, context, out _));
                    SkipWhitespace(s, ref pos);
                    if (pos < s.Length && s[pos] == ',')
                    {
                        pos++;
                        continue;
                    }

                    if (pos < s.Length && s[pos] == '>')
                    {
                        pos++;
                        break;
                    }

                    throw new ReplException("expected ',' or '>' in generic type arguments");
                }

                if (!name.Contains('`'))
                {
                    name += "`" + args.Count.ToString(CultureInfo.InvariantCulture);
                }

                var definition = context.Resolver.Resolve(name, asm);
                if (!definition.IsGenericTypeDefinition)
                {
                    throw new ReplException($"'{TypeNameFormatter.Pretty(definition)}' is not a generic type definition");
                }

                if (definition.GetGenericArguments().Length != args.Count)
                {
                    throw new ReplException($"'{TypeNameFormatter.Pretty(definition)}' takes {definition.GetGenericArguments().Length} type argument(s), not {args.Count}");
                }

                t = definition.MakeGenericType([.. args]);
            }
            else
            {
                t = asm is null && Primitives.TryGetValue(name, out var primitive) ? primitive : context.Resolver.Resolve(name, asm);
            }
        }

        // Suffixes: [] [,] [0...] & * pinned modreq(T) modopt(T)
        while (pos < s.Length)
        {
            if (s[pos] == '[' && IsArraySuffix(s, pos, out var close))
            {
                var inner = s.Substring(pos + 1, close - pos - 1).Replace(" ", "", StringComparison.Ordinal);
                var rank = inner.Length == 0 ? 1 : inner.Count(c => c == ',') + 1;
                t = inner.Length == 0 ? t.MakeArrayType() : t.MakeArrayType(rank);
                pos = close + 1;
                continue;
            }

            if (s[pos] == '&')
            {
                t = t.MakeByRefType();
                pos++;
                continue;
            }

            if (s[pos] == '*')
            {
                t = t.MakePointerType();
                pos++;
                continue;
            }

            var before = pos;
            SkipWhitespace(s, ref pos);
            if (TryKeyword(s, ref pos, "pinned"))
            {
                pinned = true;
            }
            else if (TryKeyword(s, ref pos, "modreq") || TryKeyword(s, ref pos, "modopt"))
            {
                var required = s[(pos - 6)..pos] == "modreq";
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '(')
                {
                    throw new ReplException("expected '(' after modreq/modopt");
                }

                pos++;
                var modifier = ParseAt(s, ref pos, context, out _);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ')')
                {
                    throw new ReplException("expected ')' after modreq/modopt type");
                }

                pos++;
                (required ? requiredModifiers : optionalModifiers).Add(modifier);
            }
            else
            {
                pos = before;
                break;
            }
        }

        return t;
    }

    private static bool IsArraySuffix(string s, int open, out int close)
    {
        close = s.IndexOf(']', open);
        if (close < 0)
        {
            return false;
        }

        for (var i = open + 1; i < close; i++)
        {
            if (!(char.IsDigit(s[i]) || s[i] is ',' or '.' or ' '))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits a comma-separated list while respecting nested brackets.
    /// </summary>
    /// <param name="s">The list text.</param>
    /// <returns>The trimmed items.</returns>
    public static List<string> SplitTopLevel(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < s.Length; i++)
        {
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
                    parts.Add(s[start..i].Trim());
                    start = i + 1;
                    break;
                default:
                    break;
            }
        }

        var last = s[start..].Trim();
        if (last.Length > 0)
        {
            parts.Add(last);
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

    private static Type ParseFunctionPointer(string s, ref int pos, ParseContext context)
    {
        // method [callconv] RetType *(Params)
        // Reflection has no public API to construct a function pointer type, so the parsed
        // signature is validated and the type is represented as native int, which is how the
        // evaluation stack treats a method pointer anyway.
        SkipWhitespace(s, ref pos);
        while (TryKeyword(s, ref pos, "instance") || TryKeyword(s, ref pos, "explicit") || TryKeyword(s, ref pos, "unmanaged")
            || TryKeyword(s, ref pos, "cdecl") || TryKeyword(s, ref pos, "stdcall") || TryKeyword(s, ref pos, "thiscall")
            || TryKeyword(s, ref pos, "fastcall") || TryKeyword(s, ref pos, "vararg") || TryKeyword(s, ref pos, "default"))
        {
            SkipWhitespace(s, ref pos);
        }

        // The return type ends at the '*' that is followed by the parameter list.
        var star = -1;
        for (var i = pos; i < s.Length; i++)
        {
            if (s[i] != '*')
            {
                continue;
            }

            var j = i + 1;
            SkipWhitespace(s, ref j);
            if (j < s.Length && s[j] == '(')
            {
                star = i;
                break;
            }
        }

        if (star < 0)
        {
            throw new ReplException("expected '*(' in function pointer type (method RetType *(Params))");
        }

        Parse(s[pos..star], context);
        pos = star + 1;
        SkipWhitespace(s, ref pos);
        var close = FindMatchingParen(s, pos);
        var inner = s.Substring(pos + 1, close - pos - 1);
        foreach (var part in SplitTopLevel(inner))
        {
            if (part != "...")
            {
                Parse(part, context);
            }
        }

        pos = close + 1;
        return typeof(nint);
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
            if (s[i] == '(')
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

    /// <summary>
    /// Returns the primitive type an IL keyword names, or null when the word is not a primitive keyword.
    /// </summary>
    /// <param name="keyword">The keyword, for example <c>int32</c>.</param>
    /// <returns>The type, or null.</returns>
    public static Type? PrimitiveKeywordType(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        return Primitives.TryGetValue(keyword.Trim(), out var type) ? type : null;
    }

    /// <summary>
    /// Returns the IL keyword for a primitive type, or null when the type has none.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The keyword, for example <c>int32</c>, or null.</returns>
    public static string? PrimitiveKeyword(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type == typeof(void)) { return "void"; }
        if (type == typeof(bool)) { return "bool"; }
        if (type == typeof(char)) { return "char"; }
        if (type == typeof(sbyte)) { return "int8"; }
        if (type == typeof(byte)) { return "uint8"; }
        if (type == typeof(short)) { return "int16"; }
        if (type == typeof(ushort)) { return "uint16"; }
        if (type == typeof(int)) { return "int32"; }
        if (type == typeof(uint)) { return "uint32"; }
        if (type == typeof(long)) { return "int64"; }
        if (type == typeof(ulong)) { return "uint64"; }
        if (type == typeof(float)) { return "float32"; }
        if (type == typeof(double)) { return "float64"; }
        if (type == typeof(string)) { return "string"; }
        if (type == typeof(object)) { return "object"; }
        if (type == typeof(nint)) { return "native int"; }
        if (type == typeof(nuint)) { return "native uint"; }
        if (type == typeof(TypedReference)) { return "typedref"; }
        return null;
    }
}
