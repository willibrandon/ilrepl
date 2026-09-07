using System.Globalization;
using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Parses <c>.field</c> lines: <c>[offset] attrs type name [= constant]</c>. Access follows ILAsm:
/// no access word means privatescope.
/// </summary>
public static class FieldDeclarationParser
{
    private const string Usage = "usage: .field [public] [static] [initonly|literal] T name [= int32(5)]";

    /// <summary>
    /// Parses the text after <c>.field</c>.
    /// </summary>
    /// <param name="spec">The declaration text.</param>
    /// <param name="context">The parse context of the open type, with its generic parameters in scope.</param>
    /// <param name="source">The line as typed.</param>
    /// <returns>The field, without the checks that need the whole type.</returns>
    /// <exception cref="ReplException">The line is malformed or uses a feature session fields do not have.</exception>
    public static FieldDeclaration Parse(string spec, ParseContext context, string source)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        var s = TypeParser.Normalize(spec).Trim();
        var pos = 0;
        int? offset = null;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                throw new ReplException("unterminated '[' before the field type");
            }

            var inner = s[1..close].Trim();
            if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                if (parsed < 0)
                {
                    throw new ReplException("a field offset cannot be negative");
                }

                offset = parsed;
                pos = close + 1;
            }
        }

        var attributes = FieldAttributes.PrivateScope;
        var accessSeen = false;
        while (true)
        {
            TypeParser.SkipWhitespace(s, ref pos);
            var word = PeekWord(s, pos);
            FieldAttributes? access = word switch
            {
                "public" => FieldAttributes.Public,
                "private" => FieldAttributes.Private,
                "family" => FieldAttributes.Family,
                "assembly" => FieldAttributes.Assembly,
                "famandassem" => FieldAttributes.FamANDAssem,
                "famorassem" => FieldAttributes.FamORAssem,
                "privatescope" => FieldAttributes.PrivateScope,
                _ => null,
            };
            if (access is { } a)
            {
                if (accessSeen)
                {
                    throw new ReplException("a field has one access word");
                }

                accessSeen = true;
                attributes = (attributes & ~FieldAttributes.FieldAccessMask) | a;
                pos += word.Length;
                continue;
            }

            switch (word)
            {
                case "static":
                    attributes |= FieldAttributes.Static;
                    break;
                case "initonly":
                    attributes |= FieldAttributes.InitOnly;
                    break;
                case "literal":
                    attributes |= FieldAttributes.Literal;
                    break;
                case "specialname":
                    attributes |= FieldAttributes.SpecialName;
                    break;
                case "rtspecialname":
                    attributes |= FieldAttributes.RTSpecialName;
                    break;
                case "notserialized":
                    attributes |= (FieldAttributes)0x80; // notserialized
                    break;
                case "marshal":
                case "at":
                case "pinvokeimpl":
                case "flags":
                    throw new ReplException($"'{word}' is not supported on session fields");
                default:
                    word = "";
                    break;
            }

            if (word.Length == 0)
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

        var equals = IndexOfTopLevelEquals(s, pos);
        var declaration = equals < 0 ? s[pos..] : s[pos..equals];
        var constantText = equals < 0 ? null : s[(equals + 1)..].Trim();
        var at = 0;
        var type = TypeParser.ParseAt(declaration, ref at, context, out var pinned, out var required, out var optional);
        if (pinned)
        {
            throw new ReplException("a field cannot be pinned");
        }

        var name = InstructionParser.Unquote(declaration[at..].Trim());
        if (name.Length == 0)
        {
            throw new ReplException(Usage);
        }

        if (!InstructionParser.IsIdentifier(name) && name != "value__")
        {
            throw new ReplException($"bad field name '{name}'");
        }

        if (type == typeof(void))
        {
            throw new ReplException("a field cannot be void");
        }

        if (attributes.HasFlag(FieldAttributes.Literal) && !attributes.HasFlag(FieldAttributes.Static))
        {
            throw new ReplException($"a literal field must be static: .field public static literal {TypeNameFormatter.Pretty(type)} {name} = ...");
        }

        if (attributes.HasFlag(FieldAttributes.Literal) && attributes.HasFlag(FieldAttributes.InitOnly))
        {
            throw new ReplException("a field is literal or initonly, not both");
        }

        if (attributes.HasFlag(FieldAttributes.Literal) && constantText is null)
        {
            throw new ReplException($"a literal field needs its value: .field public static literal {TypeNameFormatter.Pretty(type)} {name} = int32(...)");
        }

        if (offset is not null && attributes.HasFlag(FieldAttributes.Static))
        {
            throw new ReplException("a static field has no offset");
        }

        object? constant = null;
        var hasDefault = false;
        if (constantText is not null)
        {
            if (constantText.Length == 0)
            {
                throw new ReplException($"field {name} needs a value after '='");
            }

            var constantTarget = Nullable.GetUnderlyingType(type) ?? type;
            if (!(constantTarget.IsPrimitive || constantTarget.IsEnum || constantTarget == typeof(string) || constantTarget == typeof(object) || constantTarget == typeof(decimal) || !constantTarget.IsValueType))
            {
                throw new ReplException($"a constant must be a primitive, string, or enum; {TypeNameFormatter.Pretty(type)} is neither");
            }

            constant = ConstantParser.Parse(constantText, type, "field " + name);
            hasDefault = true;
            attributes |= FieldAttributes.HasDefault;
        }

        return new FieldDeclaration(name, type, attributes, offset, constant, hasDefault, required, optional, [], source);
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

    private static int IndexOfTopLevelEquals(string s, int from)
    {
        var depth = 0;
        var inString = false;
        for (var i = from; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
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
                case '=' when depth == 0:
                    return i;
                default:
                    break;
            }
        }

        return -1;
    }
}
