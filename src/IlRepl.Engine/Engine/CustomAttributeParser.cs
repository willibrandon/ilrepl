using System.Globalization;
using System.Reflection;
using System.Text;

namespace IlRepl.Engine;

/// <summary>
/// Parses <c>.custom</c> lines into semantic values. Both ildasm's blob form,
/// <c>.custom instance void [asm]Ns.Attr::.ctor(int32) = ( 01 00 05 00 00 00 00 00 )</c>, and the
/// typed form, <c>.custom instance void Attr::.ctor(int32) = { int32(5) }</c>, are accepted; a
/// blob is decoded against the constructor's signature so the attribute can be written again
/// against any assembly.
/// </summary>
public static class CustomAttributeParser
{
    /// <summary>
    /// Parses the text after <c>.custom</c>.
    /// </summary>
    /// <param name="spec">The attribute text.</param>
    /// <param name="context">The parse context.</param>
    /// <param name="source">The line as typed.</param>
    /// <returns>The attribute.</returns>
    /// <exception cref="ReplException">The constructor cannot be resolved or the arguments do not decode.</exception>
    public static CustomAttributeDeclaration Parse(string spec, ParseContext context, string source)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        var s = spec.Trim();
        var equals = IndexOfTopLevelEquals(s);
        var constructorText = equals < 0 ? s : s[..equals].Trim();
        var valueText = equals < 0 ? "" : s[(equals + 1)..].Trim();
        var resolved = MemberResolver.ResolveMethod(constructorText, context, wantConstructor: true);
        if (resolved.Method is not ConstructorInfo constructor)
        {
            throw new ReplException("a custom attribute names a constructor: .custom instance void Attr::.ctor(...) = ...");
        }

        if (!typeof(Attribute).IsAssignableFrom(constructor.DeclaringType))
        {
            throw new ReplException($"{TypeNameFormatter.Pretty(constructor.DeclaringType)} is not an attribute type");
        }

        var parameters = constructor.GetParameters();
        if (valueText.Length == 0)
        {
            if (parameters.Length > 0)
            {
                throw new ReplException($"{TypeNameFormatter.Pretty(constructor.DeclaringType)}'s constructor takes {parameters.Length} argument(s); write them after '='");
            }

            return new CustomAttributeDeclaration(constructor, [], [], [], source);
        }

        if (valueText.StartsWith('('))
        {
            if (!valueText.EndsWith(')'))
            {
                throw new ReplException("a custom attribute blob is written as = ( 01 00 ... )");
            }

            return DecodeBlob(constructor, ParseHex(valueText[1..^1]), context, source);
        }

        if (valueText.StartsWith('{'))
        {
            if (!valueText.EndsWith('}'))
            {
                throw new ReplException("typed attribute arguments are written as = { int32(5) string('text') }");
            }

            return ParseTyped(constructor, valueText[1..^1].Trim(), context, source);
        }

        throw new ReplException("attribute arguments follow '=' as a blob ( 01 00 ... ) or typed values { int32(5) }");
    }

    private static byte[] ParseHex(string text)
    {
        var bytes = new List<byte>();
        foreach (var token in text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length != 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                throw new ReplException($"bad byte '{token}' in attribute blob");
            }

            bytes.Add(b);
        }

        return [.. bytes];
    }

    private static CustomAttributeDeclaration DecodeBlob(ConstructorInfo constructor, byte[] blob, ParseContext context, string source)
    {
        var reader = new BlobReader(blob, context);
        if (reader.ReadUInt16() != 1)
        {
            throw new ReplException("an attribute blob starts with the prolog 01 00");
        }

        var fixedArguments = new List<object?>();
        foreach (var parameter in constructor.GetParameters())
        {
            fixedArguments.Add(reader.ReadValue(parameter.ParameterType, parameter.Name ?? "argument"));
        }

        var namedCount = reader.ReadUInt16();
        var fields = new List<(FieldInfo, object?)>();
        var properties = new List<(PropertyInfo, object?)>();
        var attributeType = constructor.DeclaringType!;
        for (var i = 0; i < namedCount; i++)
        {
            var kind = reader.ReadByte();
            var type = reader.ReadFieldOrPropType();
            var name = reader.ReadString() ?? throw new ReplException("a named attribute argument needs a name");
            var value = reader.ReadValue(type, name);
            if (kind == 0x53)
            {
                var field = attributeType.GetField(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new ReplException($"{TypeNameFormatter.Pretty(attributeType)} has no public field '{name}'");
                fields.Add((field, value));
            }
            else if (kind == 0x54)
            {
                var property = attributeType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new ReplException($"{TypeNameFormatter.Pretty(attributeType)} has no public property '{name}'");
                properties.Add((property, value));
            }
            else
            {
                throw new ReplException($"bad named argument kind {kind:X2} in attribute blob (53 is a field, 54 a property)");
            }
        }

        if (!reader.AtEnd)
        {
            throw new ReplException("attribute blob has bytes left over after the arguments");
        }

        return new CustomAttributeDeclaration(constructor, fixedArguments, fields, properties, source);
    }

    private static CustomAttributeDeclaration ParseTyped(ConstructorInfo constructor, string inner, ParseContext context, string source)
    {
        var parameters = constructor.GetParameters();
        var tokens = SplitTyped(inner);
        var fixedArguments = new List<object?>();
        var fields = new List<(FieldInfo, object?)>();
        var properties = new List<(PropertyInfo, object?)>();
        var attributeType = constructor.DeclaringType!;
        var index = 0;
        foreach (var token in tokens)
        {
            if (token.StartsWith("field ", StringComparison.Ordinal) || token.StartsWith("property ", StringComparison.Ordinal))
            {
                var isField = token.StartsWith("field ", StringComparison.Ordinal);
                var rest = token[(isField ? 6 : 9)..].Trim();
                var equals = rest.IndexOf('=', StringComparison.Ordinal);
                if (equals < 0)
                {
                    throw new ReplException("a named argument is written as field int32 Name = int32(5)");
                }

                var declaration = rest[..equals].Trim();
                var space = declaration.LastIndexOf(' ');
                var name = space < 0 ? declaration : declaration[(space + 1)..];
                var valueText = rest[(equals + 1)..].Trim();
                if (isField)
                {
                    var field = attributeType.GetField(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new ReplException($"{TypeNameFormatter.Pretty(attributeType)} has no public field '{name}'");
                    fields.Add((field, ParseTypedValue(valueText, field.FieldType, context, name)));
                }
                else
                {
                    var property = attributeType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new ReplException($"{TypeNameFormatter.Pretty(attributeType)} has no public property '{name}'");
                    properties.Add((property, ParseTypedValue(valueText, property.PropertyType, context, name)));
                }

                continue;
            }

            if (index >= parameters.Length)
            {
                throw new ReplException($"{TypeNameFormatter.Pretty(attributeType)}'s constructor takes {parameters.Length} argument(s), more were given");
            }

            fixedArguments.Add(ParseTypedValue(token, parameters[index].ParameterType, context, parameters[index].Name ?? "argument"));
            index++;
        }

        if (index != parameters.Length)
        {
            throw new ReplException($"{TypeNameFormatter.Pretty(attributeType)}'s constructor takes {parameters.Length} argument(s), {index} were given");
        }

        return new CustomAttributeDeclaration(constructor, fixedArguments, fields, properties, source);
    }

    private static object? ParseTypedValue(string text, Type target, ParseContext context, string what)
    {
        var s = text.Trim();
        if (s.StartsWith("type(", StringComparison.Ordinal) && s.EndsWith(')'))
        {
            return TypeParser.Parse(s[5..^1], context);
        }

        if (target == typeof(Type))
        {
            throw new ReplException($"{what} is a type argument; write type(Name)");
        }

        if (target == typeof(object))
        {
            // A boxed argument carries its own type: int32(5), string('x'), type(...), or bool(true).
            if (s.StartsWith("string(", StringComparison.Ordinal) && s.EndsWith(')'))
            {
                return LiteralParser.ParseString(s[7..^1]);
            }

            var paren = s.IndexOf('(', StringComparison.Ordinal);
            if (paren > 0 && s.EndsWith(')') && TypeParser.PrimitiveKeywordType(s[..paren]) is { } boxed)
            {
                return ConstantParser.Parse(s, boxed, what);
            }

            throw new ReplException($"{what} takes an object; write a typed value such as int32(5), string('x'), or type(Name)");
        }

        if (s.StartsWith("string(", StringComparison.Ordinal) && s.EndsWith(')'))
        {
            return target == typeof(string) ? LiteralParser.ParseString(s[7..^1]) : throw new ReplException($"{what} is a {TypeNameFormatter.Pretty(target)}, not a string");
        }

        if (target.IsArray)
        {
            if (!(s.StartsWith('{') && s.EndsWith('}')))
            {
                throw new ReplException($"{what} is an array; write its elements as {{ int32(1) int32(2) }}");
            }

            var element = target.GetElementType()!;
            var items = SplitTyped(s[1..^1].Trim()).Select(item => ParseTypedValue(item, element, context, what)).ToArray();
            var array = Array.CreateInstance(element, items.Length);
            for (var i = 0; i < items.Length; i++)
            {
                array.SetValue(items[i], i);
            }

            return array;
        }

        if (target.IsEnum)
        {
            var value = ConstantParser.Parse(s, target, what);
            return value is null ? null : Enum.ToObject(target, value);
        }

        return ConstantParser.Parse(s, target, what);
    }

    private static List<string> SplitTyped(string inner)
    {
        // Values are separated by whitespace, with parentheses, braces, and quotes kept together.
        var tokens = new List<string>();
        var depth = 0;
        var quote = '\0';
        var start = -1;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (c is '(' or '{' or '[')
            {
                depth++;
            }
            else if (c is ')' or '}' or ']')
            {
                depth--;
            }

            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (start >= 0)
                {
                    tokens.Add(inner[start..i]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        if (start >= 0)
        {
            tokens.Add(inner[start..]);
        }

        // "field int32 X = int32(1)" spans several whitespace-separated pieces; rejoin them.
        var merged = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is "field" or "property")
            {
                var end = i + 1;
                while (end < tokens.Count && tokens[end] != "=")
                {
                    end++;
                }

                if (end + 1 >= tokens.Count)
                {
                    throw new ReplException("a named argument is written as field int32 Name = int32(5)");
                }

                merged.Add(string.Join(" ", tokens.Skip(i).Take(end - i + 2)));
                i = end + 1;
                continue;
            }

            merged.Add(tokens[i]);
        }

        return merged;
    }

    private static int IndexOfTopLevelEquals(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '(':
                case '<':
                case '[':
                    depth++;
                    break;
                case ')':
                case '>':
                case ']':
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

    private sealed class BlobReader(byte[] blob, ParseContext context)
    {
        private int _position;

        public bool AtEnd => _position >= blob.Length;

        public byte ReadByte()
        {
            if (_position >= blob.Length)
            {
                throw new ReplException("attribute blob ends early");
            }

            return blob[_position++];
        }

        public ushort ReadUInt16() => (ushort)(ReadByte() | (ReadByte() << 8));

        public string? ReadString()
        {
            if (blob.Length > _position && blob[_position] == 0xFF)
            {
                _position++;
                return null;
            }

            var length = ReadCompressed();
            if (_position + length > blob.Length)
            {
                throw new ReplException("attribute blob ends inside a string");
            }

            var text = Encoding.UTF8.GetString(blob, _position, length);
            _position += length;
            return text;
        }

        public Type ReadFieldOrPropType()
        {
            var tag = ReadByte();
            return tag switch
            {
                0x02 => typeof(bool),
                0x03 => typeof(char),
                0x04 => typeof(sbyte),
                0x05 => typeof(byte),
                0x06 => typeof(short),
                0x07 => typeof(ushort),
                0x08 => typeof(int),
                0x09 => typeof(uint),
                0x0A => typeof(long),
                0x0B => typeof(ulong),
                0x0C => typeof(float),
                0x0D => typeof(double),
                0x0E => typeof(string),
                0x1D => ReadFieldOrPropType().MakeArrayType(),
                0x50 => typeof(Type),
                0x51 => typeof(object),
                0x55 => ResolveType(ReadString() ?? throw new ReplException("an enum argument names its type")),
                _ => throw new ReplException($"bad type tag {tag:X2} in attribute blob"),
            };
        }

        public object? ReadValue(Type type, string what)
        {
            if (type.IsEnum)
            {
                return Enum.ToObject(type, ReadValue(Enum.GetUnderlyingType(type), what)!);
            }

            if (type == typeof(bool))
            {
                return ReadByte() != 0;
            }

            if (type == typeof(char))
            {
                return (char)ReadUInt16();
            }

            if (type == typeof(sbyte))
            {
                return unchecked((sbyte)ReadByte());
            }

            if (type == typeof(byte))
            {
                return ReadByte();
            }

            if (type == typeof(short))
            {
                return unchecked((short)ReadUInt16());
            }

            if (type == typeof(ushort))
            {
                return ReadUInt16();
            }

            if (type == typeof(int))
            {
                return unchecked((int)ReadUInt32());
            }

            if (type == typeof(uint))
            {
                return ReadUInt32();
            }

            if (type == typeof(long))
            {
                return unchecked((long)ReadUInt64());
            }

            if (type == typeof(ulong))
            {
                return ReadUInt64();
            }

            if (type == typeof(float))
            {
                return BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32()));
            }

            if (type == typeof(double))
            {
                return BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64()));
            }

            if (type == typeof(string))
            {
                return ReadString();
            }

            if (type == typeof(Type))
            {
                var name = ReadString();
                return name is null ? null : ResolveType(name);
            }

            if (type == typeof(object))
            {
                return ReadValue(ReadFieldOrPropType(), what);
            }

            if (type.IsArray)
            {
                var count = ReadUInt32();
                if (count == uint.MaxValue)
                {
                    return null;
                }

                var element = type.GetElementType()!;
                var array = Array.CreateInstance(element, (int)count);
                for (var i = 0; i < count; i++)
                {
                    array.SetValue(ReadValue(element, what), i);
                }

                return array;
            }

            throw new ReplException($"{what} has type {TypeNameFormatter.Pretty(type)}, which an attribute blob cannot carry");
        }

        private uint ReadUInt32() => (uint)(ReadUInt16() | (ReadUInt16() << 16));

        private ulong ReadUInt64() => ReadUInt32() | ((ulong)ReadUInt32() << 32);

        private int ReadCompressed()
        {
            var first = ReadByte();
            if ((first & 0x80) == 0)
            {
                return first;
            }

            if ((first & 0xC0) == 0x80)
            {
                return ((first & 0x3F) << 8) | ReadByte();
            }

            return ((first & 0x1F) << 24) | (ReadByte() << 16) | (ReadByte() << 8) | ReadByte();
        }

        private Type ResolveType(string serialized)
        {
            // A type argument is stored as its assembly-qualified name; the session's own types
            // and the framework both resolve through the parser's rules.
            var comma = IndexOfTopLevelComma(serialized);
            var typeName = comma < 0 ? serialized : serialized[..comma];
            var assembly = comma < 0 ? null : new AssemblyName(serialized[(comma + 1)..].Trim()).Name;
            try
            {
                return TypeParser.Parse(assembly is null ? typeName.Replace('+', '/') : $"[{assembly}]{typeName.Replace('+', '/')}", context);
            }
            catch (ReplException)
            {
                return Type.GetType(serialized, throwOnError: false) ?? throw new ReplException($"type '{serialized}' in the attribute blob was not found");
            }
        }

        private static int IndexOfTopLevelComma(string s)
        {
            var depth = 0;
            for (var i = 0; i < s.Length; i++)
            {
                switch (s[i])
                {
                    case '[':
                        depth++;
                        break;
                    case ']':
                        depth--;
                        break;
                    case ',' when depth == 0:
                        return i;
                    default:
                        break;
                }
            }

            return -1;
        }
    }
}
