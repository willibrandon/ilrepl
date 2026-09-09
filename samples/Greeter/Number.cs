using System.Globalization;

namespace Greeter;

/// <summary>
/// Implements <see cref="IParse{TSelf}"/> with a static method.
/// </summary>
public readonly struct Number : IParse<Number>
{
    /// <inheritdoc/>
    public static int Parse(string text) => int.Parse(text, CultureInfo.InvariantCulture);
}
