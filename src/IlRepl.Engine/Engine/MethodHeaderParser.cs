namespace IlRepl.Engine;

/// <summary>
/// Parses the header of a <c>.method</c> block: <c>[modifiers] RetType Name(T name, ...) [cil managed] [{]</c>.
/// The ILAsm words around the signature are accepted so a real header pastes in, and ignored,
/// because every session method is public and static.
/// </summary>
public static class MethodHeaderParser
{
    private const string Usage = "usage: .method <return type> Name(<T name>, ...) {  (use void for no return value)";

    private static readonly string[] Modifiers =
        ["public", "private", "assembly", "family", "famandassem", "famorassem", "static", "hidebysig", "specialname", "rtspecialname"];

    private static readonly string[] Trailers = ["cil", "managed", "il", "noinlining", "aggressiveinlining", "synchronized"];

    /// <summary>
    /// Parses the text after <c>.method</c>.
    /// </summary>
    /// <param name="spec">The header text.</param>
    /// <param name="context">The parse context; its methods are the session's table, its generics are empty.</param>
    /// <param name="opensBlock">True when the header ended with <c>{</c>.</param>
    /// <returns>The signature.</returns>
    /// <exception cref="ReplException">The header is malformed, names a reserved method, or uses a feature session methods do not have.</exception>
    public static MethodSignature Parse(string spec, ParseContext context, out bool opensBlock)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        var s = TypeParser.Normalize(spec).Trim();
        opensBlock = false;
        if (s.EndsWith('{'))
        {
            opensBlock = true;
            s = s[..^1].TrimEnd();
        }

        var pos = 0;
        while (true)
        {
            TypeParser.SkipWhitespace(s, ref pos);
            if (TypeParser.TryKeyword(s, ref pos, "instance"))
            {
                throw new ReplException("session methods are static; remove 'instance'");
            }

            if (TypeParser.TryKeyword(s, ref pos, "vararg"))
            {
                throw new ReplException("session methods cannot be vararg (only the cell can, with .vararg)");
            }

            var matched = false;
            foreach (var modifier in Modifiers)
            {
                if (TypeParser.TryKeyword(s, ref pos, modifier))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                break;
            }
        }

        // The return type comes first and may itself contain parentheses (modopt, a function
        // pointer), so it is parsed as a type rather than split at the first '('. A header with
        // nothing before the name has no return type.
        var firstParen = s.IndexOf('(', pos);
        var beforeParen = (firstParen < 0 ? s[pos..] : s[pos..firstParen]).Trim();
        if (firstParen < 0 || beforeParen.Length == 0 || !beforeParen.Any(char.IsWhiteSpace))
        {
            throw new ReplException(Usage);
        }

        var returnType = TypeParser.ParseAt(s, ref pos, context, out _);
        TypeParser.SkipWhitespace(s, ref pos);
        var name = ReadName(s, ref pos);
        TypeParser.SkipWhitespace(s, ref pos);
        if (name.Length == 0 || pos >= s.Length || s[pos] != '(')
        {
            throw new ReplException(Usage);
        }

        if (!InstructionParser.IsIdentifier(name))
        {
            throw new ReplException($"bad method name '{name}'");
        }

        if (name is "Run" or "Invoke")
        {
            throw new ReplException($"'{name}' is reserved for the cell method; pick another name");
        }

        var close = TypeParser.FindMatchingParen(s, pos);
        foreach (var word in s[(close + 1)..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Trailers.Contains(word))
            {
                throw new ReplException($"unexpected '{word}' after the parameter list");
            }
        }

        var parameters = new List<ArgumentDeclaration>();
        var inner = s.Substring(pos + 1, close - pos - 1).Trim();
        if (inner.Length > 0)
        {
            foreach (var raw in TypeParser.SplitTopLevel(inner))
            {
                var part = StripParameterAttributes(raw);
                if (part == "...")
                {
                    throw new ReplException("session methods cannot be vararg (only the cell can, with .vararg)");
                }

                var at = 0;
                var type = TypeParser.ParseAt(part, ref at, context, out _);
                var parameterName = InstructionParser.Unquote(part[at..].Trim());
                if (parameterName.Length > 0 && !InstructionParser.IsIdentifier(parameterName))
                {
                    throw new ReplException($"bad parameter name '{parameterName}'");
                }

                if (parameterName.Length > 0 && parameters.Any(p => p.Name == parameterName))
                {
                    throw new ReplException($"parameter '{parameterName}' is already declared");
                }

                if (type == typeof(void))
                {
                    throw new ReplException("a parameter cannot be void");
                }

                parameters.Add(new ArgumentDeclaration(type, parameterName.Length == 0 ? null : parameterName, null, ""));
            }
        }

        return new MethodSignature(name, returnType, parameters);
    }

    private static string ReadName(string s, ref int pos)
    {
        if (pos < s.Length && s[pos] == '\'')
        {
            var end = s.IndexOf('\'', pos + 1);
            if (end < 0)
            {
                throw new ReplException("unterminated quoted name");
            }

            var quoted = s[(pos + 1)..end];
            pos = end + 1;
            return quoted;
        }

        var start = pos;
        while (pos < s.Length && s[pos] != '(' && !char.IsWhiteSpace(s[pos]))
        {
            pos++;
        }

        return s[start..pos];
    }

    private static string StripParameterAttributes(string part)
    {
        // [in], [out], and [opt] carry no meaning here. Anything else in brackets is an assembly
        // qualifier on the type and stays for the type parser.
        while (part.StartsWith('['))
        {
            var bracket = part.IndexOf(']', StringComparison.Ordinal);
            if (bracket < 0)
            {
                throw new ReplException($"bad parameter declaration '{part}'");
            }

            var attribute = part[1..bracket].Trim();
            if (attribute is not ("in" or "out" or "opt"))
            {
                break;
            }

            part = part[(bracket + 1)..].Trim();
        }

        return part;
    }
}
