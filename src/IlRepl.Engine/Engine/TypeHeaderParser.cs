using System.Globalization;
using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Parses <c>.class</c> headers.
/// </summary>
public static class TypeHeaderParser
{
    private const string Usage = "usage: .class [public] Name [extends T] [implements I, ...] {";

    /// <summary>
    /// Parses the text after <c>.class</c>.
    /// </summary>
    /// <param name="spec">The header text.</param>
    /// <param name="nested">True when the header appears inside another <c>.class</c> block.</param>
    /// <returns>The header.</returns>
    /// <exception cref="ReplException">The header is malformed or uses a word session types do not support.</exception>
    public static TypeHeader Parse(string spec, bool nested)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var s = TypeParser.Normalize(spec).Trim();
        var opens = false;
        var closes = false;
        if (s.EndsWith('}'))
        {
            var brace = s.LastIndexOf('{');
            if (brace < 0 || s[(brace + 1)..^1].Trim().Length > 0)
            {
                throw new ReplException("a .class header may end with { or { }, nothing else");
            }

            opens = true;
            closes = true;
            s = s[..brace].TrimEnd();
        }
        else if (s.EndsWith('{'))
        {
            opens = true;
            s = s[..^1].TrimEnd();
        }

        var attributes = TypeAttributes.Class;
        var visibility = (TypeAttributes?)null;
        var layout = (TypeLayoutKind?)null;
        var kind = TypeKind.Class;
        var kindFromWord = false;
        var pos = 0;
        while (true)
        {
            TypeParser.SkipWhitespace(s, ref pos);
            var word = PeekWord(s, pos);
            if (word.Length == 0)
            {
                break;
            }

            var consumed = true;
            switch (word)
            {
                case "public":
                    visibility = nested ? TypeAttributes.NestedPublic : TypeAttributes.Public;
                    break;
                case "private":
                    visibility = nested ? TypeAttributes.NestedPrivate : TypeAttributes.NotPublic;
                    break;
                case "nested":
                    pos += word.Length;
                    TypeParser.SkipWhitespace(s, ref pos);
                    var scope = PeekWord(s, pos);
                    visibility = scope switch
                    {
                        "public" => TypeAttributes.NestedPublic,
                        "private" => TypeAttributes.NestedPrivate,
                        "family" => TypeAttributes.NestedFamily,
                        "assembly" => TypeAttributes.NestedAssembly,
                        "famandassem" => TypeAttributes.NestedFamANDAssem,
                        "famorassem" => TypeAttributes.NestedFamORAssem,
                        _ => throw new ReplException("'nested' needs public, private, family, assembly, famandassem, or famorassem"),
                    };
                    if (!nested)
                    {
                        // ILAsm rewrites a nested visibility at top level to the plain one.
                        visibility = visibility is TypeAttributes.NestedPublic ? TypeAttributes.Public : TypeAttributes.NotPublic;
                    }

                    word = scope;
                    break;
                case "sealed":
                    attributes |= TypeAttributes.Sealed;
                    break;
                case "abstract":
                    attributes |= TypeAttributes.Abstract;
                    break;
                case "interface":
                    attributes |= TypeAttributes.Interface | TypeAttributes.Abstract;
                    kind = TypeKind.Interface;
                    kindFromWord = true;
                    break;
                case "value":
                    attributes |= TypeAttributes.Sealed;
                    kind = TypeKind.Struct;
                    kindFromWord = true;
                    break;
                case "enum":
                    attributes |= TypeAttributes.Sealed;
                    kind = TypeKind.Enum;
                    kindFromWord = true;
                    break;
                case "auto":
                    layout = TypeLayoutKind.Auto;
                    break;
                case "sequential":
                    layout = TypeLayoutKind.Sequential;
                    break;
                case "explicit":
                    layout = TypeLayoutKind.Explicit;
                    break;
                case "ansi":
                    attributes = (attributes & ~TypeAttributes.StringFormatMask) | TypeAttributes.AnsiClass;
                    break;
                case "unicode":
                    attributes = (attributes & ~TypeAttributes.StringFormatMask) | TypeAttributes.UnicodeClass;
                    break;
                case "autochar":
                    attributes = (attributes & ~TypeAttributes.StringFormatMask) | TypeAttributes.AutoClass;
                    break;
                case "beforefieldinit":
                    attributes |= TypeAttributes.BeforeFieldInit;
                    break;
                case "specialname":
                    attributes |= TypeAttributes.SpecialName;
                    break;
                case "serializable":
                    attributes |= (TypeAttributes)0x2000; // serializable
                    break;
                case "rtspecialname":
                    break;
                case "import":
                case "windowsruntime":
                case "extended":
                case "flags":
                    throw new ReplException($"'{word}' is not supported on session types");
                default:
                    consumed = false;
                    break;
            }

            if (!consumed)
            {
                break;
            }

            pos += word.Length;
        }

        TypeParser.SkipWhitespace(s, ref pos);
        if (pos >= s.Length)
        {
            throw new ReplException(Usage);
        }

        var fullName = ReadName(s, ref pos);
        if (fullName.Length == 0)
        {
            throw new ReplException(Usage);
        }

        var generics = new List<GenericParameterSpec>();
        TypeParser.SkipWhitespace(s, ref pos);
        if (pos < s.Length && s[pos] == '<')
        {
            var close = FindMatchingAngle(s, pos);
            generics.AddRange(GenericParameterParser.Parse(s[(pos + 1)..close]));
            pos = close + 1;
        }

        string? baseText = null;
        var interfaces = new List<string>();
        TypeParser.SkipWhitespace(s, ref pos);
        if (TypeParser.TryKeyword(s, ref pos, "extends"))
        {
            TypeParser.SkipWhitespace(s, ref pos);
            var end = IndexOfKeyword(s, pos, "implements");
            baseText = (end < 0 ? s[pos..] : s[pos..end]).Trim();
            if (baseText.Length == 0)
            {
                throw new ReplException("'extends' needs a type");
            }

            pos = end < 0 ? s.Length : end;
        }

        TypeParser.SkipWhitespace(s, ref pos);
        if (TypeParser.TryKeyword(s, ref pos, "implements"))
        {
            var list = s[pos..].Trim();
            if (list.Length == 0)
            {
                throw new ReplException("'implements' needs at least one interface");
            }

            interfaces.AddRange(TypeParser.SplitTopLevel(list));
            pos = s.Length;
        }

        TypeParser.SkipWhitespace(s, ref pos);
        if (pos < s.Length)
        {
            throw new ReplException($"unexpected '{s[pos..]}' in .class header");
        }

        if (kind == TypeKind.Interface && baseText is not null)
        {
            throw new ReplException("an interface cannot extend a class; list base interfaces after implements");
        }

        if (kind == TypeKind.Interface && layout is not null and not TypeLayoutKind.Auto)
        {
            throw new ReplException("an interface has no layout");
        }

        if (generics.Any(g => g.Attributes.HasFlag(GenericParameterAttributes.Covariant) || g.Attributes.HasFlag(GenericParameterAttributes.Contravariant)) && kind != TypeKind.Interface)
        {
            throw new ReplException("variance (+/-) is only allowed on interface type parameters");
        }

        attributes |= visibility ?? (nested ? TypeAttributes.NestedPrivate : TypeAttributes.NotPublic);
        attributes |= layout switch
        {
            TypeLayoutKind.Sequential => TypeAttributes.SequentialLayout,
            TypeLayoutKind.Explicit => TypeAttributes.ExplicitLayout,
            _ => TypeAttributes.AutoLayout,
        };

        var (ns, name) = SplitNamespace(fullName);
        var introduced = generics.Count;
        var arityWritten = name.Contains('`');
        if (introduced > 0 && !arityWritten)
        {
            // The arity suffix counts the parameters this type introduces; a nested type's
            // redeclared parameters are accounted for by the session, which knows the enclosing type.
            name += "`" + introduced.ToString(CultureInfo.InvariantCulture);
        }

        if ((ns.Length == 0 ? name : ns + "." + name).StartsWith("IlRepl.", StringComparison.Ordinal) || name == "IlRepl")
        {
            throw new ReplException("the IlRepl namespace is reserved for the cell type");
        }

        return new TypeHeader(attributes, kind, kindFromWord, layout ?? TypeLayoutKind.Auto, ns, name, generics, baseText, interfaces, opens, closes) { ArityWritten = arityWritten };
    }

    private static (string Namespace, string Name) SplitNamespace(string fullName)
    {
        var dot = fullName.LastIndexOf('.');
        return dot < 0 ? ("", fullName) : (fullName[..dot], fullName[(dot + 1)..]);
    }

    private static string PeekWord(string s, int pos)
    {
        var end = pos;
        while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] == '_'))
        {
            end++;
        }

        return s[pos..end];
    }

    private static string ReadName(string s, ref int pos)
    {
        var name = new System.Text.StringBuilder();
        while (pos < s.Length)
        {
            if (s[pos] == '\'')
            {
                var end = TypeParser.EndOfQuoted(s, pos);
                name.Append(TypeParser.DecodeQuoted(s[(pos + 1)..end]));
                pos = end + 1;
            }
            else if (TypeParser.IsNameChar(s[pos]) && s[pos] != '/')
            {
                name.Append(s[pos++]);
            }
            else
            {
                break;
            }
        }

        return name.ToString();
    }

    private static int FindMatchingAngle(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '<')
            {
                depth++;
            }
            else if (s[i] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        throw new ReplException("unbalanced '<' in generic parameter list");
    }

    private static int IndexOfKeyword(string s, int from, string keyword)
    {
        var depth = 0;
        for (var i = from; i + keyword.Length <= s.Length; i++)
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
                default:
                    break;
            }

            if (depth == 0 && (i == 0 || char.IsWhiteSpace(s[i - 1])) && string.CompareOrdinal(s, i, keyword, 0, keyword.Length) == 0
                && (i + keyword.Length == s.Length || char.IsWhiteSpace(s[i + keyword.Length])))
            {
                return i;
            }
        }

        return -1;
    }
}
