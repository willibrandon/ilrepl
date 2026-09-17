namespace IlRepl.Protocol;

/// <summary>
/// A session trampoline and its captured implementation.
/// </summary>
public sealed record NativeBinding
{
    /// <summary>
    /// The session method name.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Whether this binding is visible at the captured declaration boundary.
    /// </summary>
    public bool Visible { get; init; } = true;

    /// <summary>
    /// The stable callable method.
    /// </summary>
    public NativeMethodIdentity Trampoline { get; init; } = new();

    /// <summary>
    /// The implementation currently bound to the callable method.
    /// </summary>
    public NativeMethodIdentity Implementation { get; init; } = new();
}
