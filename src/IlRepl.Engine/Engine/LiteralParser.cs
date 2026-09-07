using System.Globalization;
using System.Text;

namespace IlRepl.Engine;

/// <summary>
/// Parses the literal forms IL allows for immediates and strings.
/// </summary>
public static class LiteralParser
{
    /// <summary>
    /// Parses an integer literal: decimal, <c>0x</c> hex, <c>0b</c> binary, or a quoted character.
    /// </summary>
    /// <param name="text">The literal text.</param>
    /// <param name="what">What the literal is for, used in error messages.</param>
    /// <returns>The value, sign-extended to 64 bits.</returns>
    /// <exception cref="ReplException">The text is not an integer literal.</exception>
    public static long ParseInteger(string text, string what)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(what);
        if (text.Length == 0)
        {
            throw new ReplException($"'{what}' needs an integer operand");
        }

        var t = text.Replace("_", "", StringComparison.Ordinal);
        var negative = t.StartsWith('-');
        if (negative || t.StartsWith('+'))
        {
            t = t[1..];
        }

        long value;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                throw new ReplException($"bad hex literal '{text}'");
            }

            value = unchecked((long)hex);
        }
        else if (t.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                value = Convert.ToInt64(t[2..], 2);
            }
            catch (FormatException)
            {
                throw new ReplException($"bad binary literal '{text}'");
            }
        }
        else if (t.Length == 3 && t[0] == '\'' && t[2] == '\'')
        {
            value = t[1];
        }
        else if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            if (ulong.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
            {
                value = unchecked((long)unsigned);
            }
            else
            {
                throw new ReplException($"'{what}' needs an integer operand, got '{text}'");
            }
        }

        return negative ? -value : value;
    }

    /// <summary>
    /// Parses a floating-point literal: a decimal number, <c>nan</c>, <c>inf</c>, or the ILAsm
    /// <c>float64(0x...)</c> and <c>float32(0x...)</c> bit-pattern forms.
    /// </summary>
    /// <param name="text">The literal text.</param>
    /// <param name="what">What the literal is for, used in error messages.</param>
    /// <returns>The value as a double.</returns>
    /// <exception cref="ReplException">The text is not a floating-point literal.</exception>
    public static double ParseFloat(string text, string what)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(what);
        if (text.Length == 0)
        {
            throw new ReplException($"'{what}' needs a numeric operand");
        }

        var t = text.Trim();
        if (t.StartsWith("float64(", StringComparison.Ordinal) && t.EndsWith(')'))
        {
            var inner = t[8..^1].Trim();
            return inner.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? BitConverter.Int64BitsToDouble(ParseInteger(inner, what))
                : ParseFloat(inner, what);
        }

        if (t.StartsWith("float32(", StringComparison.Ordinal) && t.EndsWith(')'))
        {
            var inner = t[8..^1].Trim();
            return inner.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? BitConverter.Int32BitsToSingle(unchecked((int)ParseInteger(inner, what)))
                : (float)ParseFloat(inner, what);
        }

        switch (t.ToLowerInvariant())
        {
            case "nan":
                return double.NaN;
            case "inf":
            case "+inf":
            case "infinity":
                return double.PositiveInfinity;
            case "-inf":
            case "-infinity":
                return double.NegativeInfinity;
            default:
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    return d;
                }

                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || t.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
                {
                    return ParseInteger(t, what);
                }

                throw new ReplException($"'{what}' needs a numeric operand, got '{text}'");
        }
    }

    /// <summary>
    /// Parses a <c>float32</c> literal without widening it: <c>float32(0x...)</c> reinterprets the
    /// bits exactly, so a signaling NaN stays signaling; every other spelling parses as a float.
    /// </summary>
    /// <param name="text">The literal text.</param>
    /// <param name="what">What the literal is for, used in error messages.</param>
    /// <returns>The value as a float.</returns>
    /// <exception cref="ReplException">The text is not a floating-point literal.</exception>
    public static float ParseFloat32(string text, string what)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(what);
        var t = text.Trim();
        if (t.StartsWith("float32(", StringComparison.Ordinal) && t.EndsWith(')'))
        {
            var inner = t[8..^1].Trim();
            if (inner.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                var bits = ParseInteger(inner, what);
                if (bits is < 0 or > uint.MaxValue)
                {
                    throw new ReplException($"'{what}' float32 bit pattern must fit 32 bits, got '{inner}'");
                }

                return BitConverter.Int32BitsToSingle(unchecked((int)(uint)bits));
            }

            return ParseFloat32(inner, what);
        }

        if (t.StartsWith("float64(", StringComparison.Ordinal))
        {
            return (float)ParseFloat(t, what);
        }

        switch (t.ToLowerInvariant())
        {
            case "nan":
                return float.NaN;
            case "inf":
            case "+inf":
            case "infinity":
                return float.PositiveInfinity;
            case "-inf":
            case "-infinity":
                return float.NegativeInfinity;
            default:
                if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                {
                    return f;
                }

                return (float)ParseFloat(t, what);
        }
    }

    /// <summary>
    /// Parses a string operand: a double-quoted string with C-style escapes, a single-quoted string,
    /// an ILAsm <c>bytearray (…)</c> of UTF-16 bytes, or, as a convenience, bare text.
    /// </summary>
    /// <param name="text">The operand text.</param>
    /// <returns>The string value.</returns>
    /// <exception cref="ReplException">A <c>bytearray</c> is malformed.</exception>
    public static string ParseString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim();
        if (s.StartsWith("bytearray", StringComparison.Ordinal))
        {
            var open = s.IndexOf('(', StringComparison.Ordinal);
            var close = s.LastIndexOf(')');
            if (open < 0 || close < open)
            {
                throw new ReplException("bytearray needs parentheses: bytearray (48 00 69 00)");
            }

            var bytes = new List<byte>();
            foreach (var hex in s[(open + 1)..close].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    throw new ReplException($"bad byte '{hex}' in bytearray");
                }

                bytes.Add(b);
            }

            if (bytes.Count % 2 != 0)
            {
                bytes.Add(0);
            }

            return Encoding.Unicode.GetString([.. bytes]);
        }

        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            s = s[1..^1];
        }
        else if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
        {
            s = s[1..^1];
        }
        else
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                continue;
            }

            var n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '"': sb.Append('"'); break;
                case '\'': sb.Append('\''); break;
                case '?': sb.Append('?'); break;
                case '\\': sb.Append('\\'); break;
                case 'u' when i + 4 < s.Length && ushort.TryParse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                case 'x' when i + 2 < s.Length && byte.TryParse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexByte):
                    sb.Append((char)hexByte);
                    i += 2;
                    break;
                default:
                    if (n is >= '0' and <= '7')
                    {
                        // Octal escape, up to three digits.
                        var value = 0;
                        var digits = 0;
                        while (digits < 3 && i < s.Length && s[i] is >= '0' and <= '7')
                        {
                            value = (value * 8) + (s[i] - '0');
                            i++;
                            digits++;
                        }

                        i--;
                        sb.Append((char)value);
                    }
                    else
                    {
                        sb.Append('\\').Append(n);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quotes and escapes a string for display or for ILAsm output.
    /// </summary>
    /// <param name="value">The string.</param>
    /// <returns>The quoted string.</returns>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                case '\0': sb.Append("\\0"); break;
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }
}
