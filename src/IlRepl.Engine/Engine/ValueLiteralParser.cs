using System.Globalization;

namespace IlRepl.Engine;

/// <summary>
/// Parses the literal written after <c>=</c> in an <c>.args</c> declaration into a value of the
/// declared type.
/// </summary>
public static class ValueLiteralParser
{
    /// <summary>
    /// Parses a literal for the given type. Supports <c>null</c>, booleans, characters, strings,
    /// all integer and floating-point primitives, <c>decimal</c>, native integers, and enum names.
    /// </summary>
    /// <param name="literal">The literal text.</param>
    /// <param name="type">The declared argument type.</param>
    /// <returns>The value, or null for <c>null</c>.</returns>
    /// <exception cref="ReplException">The literal does not fit the type.</exception>
    public static object? Parse(string literal, Type type)
    {
        ArgumentNullException.ThrowIfNull(literal);
        ArgumentNullException.ThrowIfNull(type);
        var s = literal.Trim();
        if (s == "null")
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            {
                throw new ReplException($"null is not a valid {TypeNameFormatter.Pretty(type)}");
            }

            return null;
        }

        var target = Nullable.GetUnderlyingType(type) ?? type;
        try
        {
            if (target == typeof(string))
            {
                return LiteralParser.ParseString(s);
            }

            if (target == typeof(bool))
            {
                return s.ToLowerInvariant() switch
                {
                    "true" or "1" => true,
                    "false" or "0" => false,
                    _ => throw new ReplException($"'{s}' is not a bool"),
                };
            }

            if (target == typeof(char))
            {
                var text = LiteralParser.ParseString(s);
                return text.Length == 1 ? text[0] : throw new ReplException($"'{s}' is not a single character");
            }

            if (target.IsEnum)
            {
                return Enum.Parse(target, s, ignoreCase: true);
            }

            if (target == typeof(float))
            {
                return (float)LiteralParser.ParseFloat(s, "argument");
            }

            if (target == typeof(double))
            {
                return LiteralParser.ParseFloat(s, "argument");
            }

            if (target == typeof(decimal))
            {
                return decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture);
            }

            if (target == typeof(object))
            {
                return ParseObject(s);
            }

            if (target.IsPrimitive || target == typeof(nint) || target == typeof(nuint))
            {
                var value = LiteralParser.ParseInteger(s, "argument");
                return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            throw new ReplException($"'{s}' does not fit {TypeNameFormatter.Pretty(type)}: {ex.Message}", ex);
        }

        throw new ReplException($"cannot write a literal of type {TypeNameFormatter.Pretty(type)}; use a primitive, string, enum, or null");
    }

    private static object ParseObject(string s)
    {
        if (s.StartsWith('"') || s.StartsWith('\''))
        {
            return LiteralParser.ParseString(s);
        }

        if (s is "true" or "false")
        {
            return s == "true";
        }

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return l is >= int.MinValue and <= int.MaxValue ? (int)l : l;
        }

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        return s;
    }
}
