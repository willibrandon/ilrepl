namespace Greeter;

/// <summary>
/// A type with a nested type, to exercise <c>Outer/Inner</c> references.
/// </summary>
public static class Outer
{
    /// <summary>
    /// The nested type.
    /// </summary>
    public static class Inner
    {
        /// <summary>
        /// A value only reachable through the nested type.
        /// </summary>
        public static string Name => "inner";
    }
}
