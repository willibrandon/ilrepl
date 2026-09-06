namespace IlRepl.Engine;

/// <summary>
/// Parses the header of a <c>.method</c> block: <c>[modifiers] RetType Name(T name, ...) [cil managed] [{]</c>.
/// The ILAsm words around the signature are accepted so a real header pastes in, and ignored,
/// because every session method is public and static.
/// </summary>
public static class MethodHeaderParser
{
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

        var paren = s.IndexOf('(', StringComparison.Ordinal);
        if (paren < 0)
        {
            throw new ReplException("usage: .method <return type> Name(<T name>, ...) {  (use void for no return value)");
        }

        var close = TypeParser.FindMatchingParen(s, paren);
        foreach (var word in s[(close + 1)..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Trailers.Contains(word))
            {
                throw new ReplException($"unexpected '{word}' after the parameter list");
            }
        }

        var head = s[..paren].Trim();
        var pos = 0;
        while (true)
        {
            TypeParser.SkipWhitespace(head, ref pos);
            if (TypeParser.TryKeyword(head, ref pos, "instance"))
            {
                throw new ReplException("session methods are static; remove 'instance'");
            }

            if (TypeParser.TryKeyword(head, ref pos, "vararg"))
            {
                throw new ReplException("session methods cannot be vararg (only the cell can, with .vararg)");
            }

            var matched = false;
            foreach (var modifier in Modifiers)
            {
                if (TypeParser.TryKeyword(head, ref pos, modifier))
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

        head = head[pos..].Trim();
        var space = head.LastIndexOfAny([' ', '\t']);
        if (space < 0)
        {
            throw new ReplException("usage: .method <return type> Name(<T name>, ...) {  (use void for no return value)");
        }

        var name = Unquote(head[(space + 1)..].Trim());
        var returnText = head[..space].Trim();
        if (!InstructionParser.IsIdentifier(name))
        {
            throw new ReplException($"bad method name '{name}'");
        }

        if (name is "Run" or "Invoke")
        {
            throw new ReplException($"'{name}' is reserved for the cell method; pick another name");
        }

        var returnType = TypeParser.Parse(returnText, context);
        var parameters = new List<ArgumentDeclaration>();
        var inner = s.Substring(paren + 1, close - paren - 1).Trim();
        if (inner.Length > 0)
        {
            foreach (var raw in TypeParser.SplitTopLevel(inner))
            {
                var part = raw;
                if (part == "...")
                {
                    throw new ReplException("session methods cannot be vararg (only the cell can, with .vararg)");
                }

                // [in], [out], and [opt] carry no meaning here; strip them the way .locals strips [N].
                while (part.StartsWith('['))
                {
                    var bracket = part.IndexOf(']', StringComparison.Ordinal);
                    if (bracket < 0)
                    {
                        throw new ReplException($"bad parameter declaration '{raw}'");
                    }

                    part = part[(bracket + 1)..].Trim();
                }

                var at = 0;
                var type = TypeParser.ParseAt(part, ref at, context, out _);
                var parameterName = Unquote(part[at..].Trim());
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

    private static string Unquote(string name) =>
        name.StartsWith('\'') && name.EndsWith('\'') && name.Length > 2 ? name[1..^1] : name;
}
