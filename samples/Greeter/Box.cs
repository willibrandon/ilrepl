namespace Greeter;

/// <summary>
/// A generic container, for <c>!0</c> in member references and generic instantiation.
/// </summary>
/// <typeparam name="T">The contained type.</typeparam>
public sealed class Box<T>
{
    /// <summary>
    /// The contained value.
    /// </summary>
    public T Value;

    /// <summary>
    /// Creates a box.
    /// </summary>
    /// <param name="value">The value.</param>
    public Box(T value)
    {
        Value = value;
    }

    /// <summary>
    /// Returns the value.
    /// </summary>
    /// <returns>The value.</returns>
    public T Get() => Value;

    /// <summary>
    /// Maps the value into a new box.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="map">The mapping.</param>
    /// <returns>A box of the mapped value.</returns>
    public Box<TResult> Map<TResult>(Func<T, TResult> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return new Box<TResult>(map(Value));
    }
}
