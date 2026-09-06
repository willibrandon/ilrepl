namespace Greeter;

/// <summary>
/// A shape implemented by a class.
/// </summary>
/// <param name="side">The side length.</param>
public sealed class Square(double side) : IShape
{
    /// <inheritdoc />
    public double Area() => side * side;
}
