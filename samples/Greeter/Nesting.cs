namespace Greeter;

/// <summary>
/// Exercises nested-type accessibility against the runtime's verdicts.
/// </summary>
/// <remarks>
/// Nested types of every visibility, and methods that take them, so accessibility from a cell and
/// from a derived session type can be judged against what the runtime allows.
/// </remarks>
public partial class Nesting
{

    /// <summary>
    /// Returns its argument, so any nested type can be named as its argument.
    /// </summary>
    /// <typeparam name="T">The argument type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The value.</returns>
    public static T Echo<T>(T value) => value;

    /// <summary>
    /// Takes the public nested type.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>True.</returns>
    public static bool TakePublic(PublicNested value) => value is not null;

    /// <summary>
    /// Takes the protected nested type.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>True.</returns>
    protected static bool TakeProtected(ProtectedNested value) => value is not null;

    internal static bool TakeInternal(InternalNested value) => value is not null;

    private static bool TakePrivate(PrivateNested value) => value is not null;

    /// <summary>
    /// Uses every private member so nothing here is unused.
    /// </summary>
    /// <returns>True.</returns>
    public static bool Exercise() => TakePrivate(new PrivateNested()) && TakeInternal(new InternalNested()) && TakeProtected(
        new ProtectedNested()) && Echo(new PrivateProtectedNested()) is not null && Echo(new ProtectedInternalNested()) is not null;
}
