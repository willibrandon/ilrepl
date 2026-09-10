namespace Greeter;

/// <summary>
/// Generic methods with every constraint shape, and two overloads that differ only in arity.
/// </summary>
public static class Generic
{
    /// <summary>
    /// One type parameter, no ordinary parameters.
    /// </summary>
    /// <typeparam name="T">The parameter.</typeparam>
    /// <returns>The parameter's name.</returns>
    public static string M<T>() => typeof(T).Name;

    /// <summary>
    /// Two type parameters, no ordinary parameters.
    /// </summary>
    /// <typeparam name="T">The first parameter.</typeparam>
    /// <typeparam name="U">The second parameter.</typeparam>
    /// <returns>The parameters' names.</returns>
    public static string M<T, U>() => typeof(T).Name + "," + typeof(U).Name;

    /// <summary>
    /// Constrained to value types.
    /// </summary>
    /// <typeparam name="T">A value type.</typeparam>
    /// <returns>The default value.</returns>
    public static T Constrained<T>() where T : struct => default;

    /// <summary>
    /// Constrained to reference types.
    /// </summary>
    /// <typeparam name="T">A reference type.</typeparam>
    /// <returns>Null.</returns>
    public static T? Boxed<T>() where T : class => null;

    /// <summary>
    /// Constrained to types with a parameterless constructor.
    /// </summary>
    /// <typeparam name="T">A constructible type.</typeparam>
    /// <returns>A new instance.</returns>
    public static T Fresh<T>() where T : new() => new();

    /// <summary>
    /// A constraint that depends on the other parameter.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <typeparam name="U">A sequence of the element type.</typeparam>
    /// <returns>Zero.</returns>
    public static int Dependent<T, U>() where U : IEnumerable<T> => 0;
}
