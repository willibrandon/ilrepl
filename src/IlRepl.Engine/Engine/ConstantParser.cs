using System.Globalization;

namespace IlRepl.Engine;

/// <summary>
/// Parses the constant forms ILAsm writes after <c>=</c>: <c>int32(5)</c>, <c>uint8(1)</c>,
/// <c>float64(1.5)</c>, <c>char(65)</c>, <c>bool(true)</c>, <c>"text"</c>, <c>nullref</c>,
/// <c>bytearray(...)</c>, and, as a convenience, a bare literal typed by the target.
/// </summary>
public static class ConstantParser
{
    private static readonly Dictionary<string, Type> Wrappers = new(StringComparer.Ordinal)
    {
        ["int8"] = typeof(sbyte), ["uint8"] = typeof(byte), ["int16"] = typeof(short), ["uint16"] = typeof(ushort),
        ["int32"] = typeof(int), ["uint32"] = typeof(uint), ["int64"] = typeof(long), ["uint64"] = typeof(ulong),
        ["float32"] = typeof(float), ["float64"] = typeof(double), ["char"] = typeof(char), ["bool"] = typeof(bool),
    };

    /// <summary>
    /// Parses a constant for a field, parameter, or property of the given type.
    /// </summary>
    /// <param name="text">The text after <c>=</c>.</param>
    /// <param name="target">The declared type; an enum takes its underlying type's wrapper.</param>
    /// <param name="what">What the constant is for, used in messages.</param>
    /// <returns>The value, or null for <c>nullref</c>.</returns>
    /// <exception cref="ReplException">The text is not a constant, or its type does not fit the target.</exception>
    public static object? Parse(string text, Type target, string what)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(what);
        var s = TypeParser.Normalize(text).Trim();
        if (s.Length == 0)
        {
            throw new ReplException($"{what} needs a value after '='");
        }

        var expected = Nullable.GetUnderlyingType(target) ?? target;
        if (expected.IsEnum)
        {
            expected = Enum.GetUnderlyingType(expected);
        }

        if (s == "nullref")
        {
            if (target.IsValueType && Nullable.GetUnderlyingType(target) is null)
            {
                throw new ReplException($"nullref is not a valid {TypeNameFormatter.Pretty(target)}");
            }

            return null;
        }

        if (s.StartsWith('"') || s.StartsWith("bytearray", StringComparison.Ordinal))
        {
            if (expected != typeof(string) && expected != typeof(object))
            {
                throw new ReplException($"{what} is a {TypeNameFormatter.Pretty(target)}; write {TypeParser.PrimitiveKeyword(expected) ?? TypeNameFormatter.Pretty(expected)}(...)");
            }

            return LiteralParser.ParseString(s);
        }

        var paren = s.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0 && s.EndsWith(')') && Wrappers.TryGetValue(s[..paren].Trim(), out var wrapper))
        {
            var inner = s[(paren + 1)..^1].Trim();
            if (wrapper != expected && expected != typeof(object))
            {
                var keyword = TypeParser.PrimitiveKeyword(expected) ?? TypeNameFormatter.Pretty(expected);
                throw new ReplException(target.IsEnum
                    ? $"{what} is a {TypeNameFormatter.Pretty(target)}; write {keyword}(...) for its underlying type"
                    : $"{what} is a {TypeNameFormatter.Pretty(target)}, not {s[..paren].Trim()}; write {keyword}(...)");
            }

            return Convert(wrapper, inner, what);
        }

        // A bare literal is typed by the target, which is how .args already reads its values.
        if (expected == typeof(object))
        {
            throw new ReplException($"{what} needs a typed constant such as int32(5), \"text\", or nullref");
        }

        return ValueLiteralParser.Parse(s, expected);
    }

    private static object Convert(Type wrapper, string inner, string what)
    {
        try
        {
            if (wrapper == typeof(bool))
            {
                return inner switch
                {
                    "true" => true,
                    "false" => false,
                    _ => throw new ReplException($"bool needs true or false, not '{inner}'"),
                };
            }

            if (wrapper == typeof(float))
            {
                return (float)LiteralParser.ParseFloat(inner, what);
            }

            if (wrapper == typeof(double))
            {
                return LiteralParser.ParseFloat(inner, what);
            }

            if (wrapper == typeof(char))
            {
                return (char)checked((ushort)LiteralParser.ParseInteger(inner, what));
            }

            var value = LiteralParser.ParseInteger(inner, what);
            return wrapper == typeof(ulong)
                ? unchecked((ulong)value)
                : System.Convert.ChangeType(value, wrapper, CultureInfo.InvariantCulture);
        }
        catch (OverflowException ex)
        {
            throw new ReplException($"'{inner}' does not fit {TypeParser.PrimitiveKeyword(wrapper)}", ex);
        }
    }
}
