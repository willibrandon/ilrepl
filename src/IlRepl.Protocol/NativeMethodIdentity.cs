namespace IlRepl.Protocol;

/// <summary>
/// A closed method identity independent of a process address.
/// </summary>
public sealed record NativeMethodIdentity
{
    /// <summary>
    /// The full assembly identity.
    /// </summary>
    public string Assembly { get; init; } = "";

    /// <summary>
    /// The declaring type definition name.
    /// </summary>
    public string Type { get; init; } = "";

    /// <summary>
    /// The metadata method token.
    /// </summary>
    public int Token { get; init; } = 0;

    /// <summary>
    /// The assembly-qualified declaring type arguments.
    /// </summary>
    public string[] TypeArguments { get; init; } = [];

    /// <summary>
    /// The assembly-qualified method arguments.
    /// </summary>
    public string[] MethodArguments { get; init; } = [];

    /// <summary>
    /// The complete concrete and canonical CoreCLR listing signatures derived from metadata.
    /// </summary>
    public string[] JitNames { get; init; } = [];

    /// <summary>
    /// The user-facing method signature.
    /// </summary>
    public string DisplayName { get; init; } = "";

    /// <summary>
    /// Whether the closed return signature can carry a reference, pointer, or runtime handle.
    /// </summary>
    public bool ReturnsPointer { get; init; } = false;
}
