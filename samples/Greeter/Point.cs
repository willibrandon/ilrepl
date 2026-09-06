namespace Greeter;

/// <summary>
/// A value type with fields, a constructor, and an instance method.
/// </summary>
public struct Point
{
    /// <summary>
    /// The x coordinate.
    /// </summary>
    public int X;

    /// <summary>
    /// The y coordinate.
    /// </summary>
    public int Y;

    /// <summary>
    /// Creates a point.
    /// </summary>
    /// <param name="x">The x coordinate.</param>
    /// <param name="y">The y coordinate.</param>
    public Point(int x, int y)
    {
        X = x;
        Y = y;
    }

    /// <summary>
    /// The Manhattan distance from the origin.
    /// </summary>
    /// <returns>The distance.</returns>
    public readonly int Manhattan() => Math.Abs(X) + Math.Abs(Y);

    /// <inheritdoc />
    public override readonly string ToString() => $"({X}, {Y})";
}
