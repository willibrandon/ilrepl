using System.Globalization;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Validates the numeric value of a .pack or .size declaration without changing a type.
/// </summary>
internal static class LayoutDirectiveParser
{
    /// <summary>
    /// Parses the layout value and enforces the CLI packing and size bounds.
    /// </summary>
    /// <param name="text">The text after the directive.</param>
    /// <param name="pack">Whether this is a .pack declaration.</param>
    /// <returns>The declared value.</returns>
    public static int Parse(string text, bool pack)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new ReplException(pack ? ".pack must be 0 or a power of two up to 128" : ".size must be a non-negative integer");
        }

        if (pack && (value > 128 || (value & (value - 1)) != 0))
        {
            throw new ReplException(".pack must be 0 or a power of two up to 128");
        }

        return value;
    }
}
