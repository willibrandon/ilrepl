namespace Greeter;

/// <summary>
/// A shape implemented by a struct, for <c>constrained.</c> callvirt on a value type.
/// </summary>
/// <param name="radius">The radius.</param>
public readonly struct Circle(double radius) : IShape
{
    /// <inheritdoc />
    public double Area() => Math.PI * radius * radius;
}
