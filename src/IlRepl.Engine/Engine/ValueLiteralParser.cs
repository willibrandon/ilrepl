using System.Globalization;
using System.Numerics;

namespace IlRepl.Engine;

/// <summary>
/// Parses argument literals into values of their declared types.
/// </summary>
public static class ValueLiteralParser
{
    /// <summary>
    /// Parses null, boolean, character, string, numeric, native integer, and enum literals for the given type.
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
                return LiteralParser.ParseFloat32(s, "argument");
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
                var value = ParseInteger(s);
                if (target == typeof(nint))
                {
                    return checked((nint)(long)value);
                }

                if (target == typeof(nuint))
                {
                    return checked((nuint)(ulong)value);
                }

                return target == typeof(ulong) ? (ulong)value : Convert.ChangeType((long)value, target, CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            throw new ReplException($"'{s}' does not fit {TypeNameFormatter.Pretty(type)}: {ex.Message}", ex);
        }

        throw new ReplException($"cannot write a literal of type {TypeNameFormatter.Pretty(type)}; use a primitive, string, enum, or null");
    }

    private static BigInteger ParseInteger(string literal)
    {
        var text = literal.Replace("_", "", StringComparison.Ordinal);
        var negative = text.StartsWith('-');
        if (negative || text.StartsWith('+'))
        {
            text = text[1..];
        }

        var hexadecimal = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var binary = text.StartsWith("0b", StringComparison.OrdinalIgnoreCase);
        if ((hexadecimal || binary) && text.Length == 2)
        {
            throw new FormatException("a numeric base prefix must be followed by digits");
        }

        var value = hexadecimal
            ? BigInteger.Parse("0" + text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : binary ? BigInteger.Parse("0" + text[2..], NumberStyles.AllowBinarySpecifier, CultureInfo.InvariantCulture)
                : text.Length == 3 && text[0] == '\'' && text[2] == '\'' ? text[1]
                    : BigInteger.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative)
        {
            value = -value;
        }

        return value;
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
