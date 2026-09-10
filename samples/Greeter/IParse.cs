namespace Greeter;

/// <summary>
/// An interface with a static abstract member, which a cell reaches through <c>constrained.</c> and <c>call</c>.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
public interface IParse<TSelf> where TSelf : IParse<TSelf>
{
    /// <summary>
    /// Parses a number.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The number.</returns>
    static abstract int Parse(string text);
}
