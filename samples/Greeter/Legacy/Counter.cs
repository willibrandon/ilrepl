namespace Greeter.Legacy;

/// <summary>
/// A second <c>Counter</c> in another namespace, so the short name is ambiguous once the sample is loaded.
/// </summary>
public sealed class Counter
{
    /// <summary>
    /// The current count.
    /// </summary>
    public int Count;

    /// <summary>
    /// Adds one.
    /// </summary>
    public void Bump() => Count++;
}
