namespace IlRepl.Engine;

/// <summary>
/// Marks a stack entry pushed by <c>box</c>: an object reference to a value of type <typeparamref name="T"/>.
/// </summary>
/// <remarks>
/// It renders as <c>object</c>, and it can be returned wherever a <typeparamref name="T"/> instance is assignable, so a boxed int32
/// satisfies <c>IComparable</c> but not <c>string</c>.
/// </remarks>
/// <typeparam name="T">The boxed value type.</typeparam>
public static class Boxed<T>
{
}
