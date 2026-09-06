namespace Greeter;

/// <summary>
/// Methods that throw, for exception block tests.
/// </summary>
public static class Thrower
{
    /// <summary>
    /// Throws an <see cref="InvalidOperationException"/>.
    /// </summary>
    public static void Boom() => throw new InvalidOperationException("boom");

    /// <summary>
    /// Throws when the argument is negative, otherwise returns it.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The value when it is not negative.</returns>
    public static int Check(int value) => value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
}
