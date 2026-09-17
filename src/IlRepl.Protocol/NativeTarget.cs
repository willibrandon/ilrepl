namespace IlRepl.Protocol;

/// <summary>
/// The exact implementation and immutable declaration context supplied to a worker.
/// </summary>
public sealed record NativeTarget
{
    /// <summary>
    /// The exact native dependency images supplied to the worker.
    /// </summary>
    public ComparisonNativeLibrary[] NativeLibraries { get; init; } = [];

    /// <summary>
    /// The requested selector.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// The captured source or image fingerprint.
    /// </summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>
    /// The selected image-backed method, absent for a cell.
    /// </summary>
    public NativeMethodIdentity? Method { get; init; } = null;

    /// <summary>
    /// The reconstruction recipe for a dynamic cell.
    /// </summary>
    public NativeCell? Cell { get; init; } = null;

    /// <summary>
    /// The original assembly graph.
    /// </summary>
    public NativeAssembly[] Assemblies { get; init; } = [];

    /// <summary>
    /// The captured trampoline bindings.
    /// </summary>
    public NativeBinding[] Bindings { get; init; } = [];

    /// <summary>
    /// The session type aliases and assembly-qualified identities.
    /// </summary>
    public Dictionary<string, string> Types { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The editable method aliases.
    /// </summary>
    public Dictionary<string, NativeMethodIdentity> Aliases { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The selected explicit scenario body.
    /// </summary>
    public NativeMethodIdentity? Scenario { get; init; } = null;
}
