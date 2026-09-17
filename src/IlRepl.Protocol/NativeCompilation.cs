namespace IlRepl.Protocol;

/// <summary>
/// One complete attributed compilation and its original native text.
/// </summary>
public sealed record NativeCompilation
{
    /// <summary>
    /// The method signature printed by the JIT.
    /// </summary>
    public string Method { get; init; } = "";

    /// <summary>
    /// The compilation tier printed by the runtime.
    /// </summary>
    public string Tier { get; init; } = "";

    /// <summary>
    /// The observed profile provenance.
    /// </summary>
    public string Pgo { get; init; } = "Unknown";

    /// <summary>
    /// The native byte count.
    /// </summary>
    public int CodeSize { get; init; } = 0;

    /// <summary>
    /// The runtime method identity from EventPipe.
    /// </summary>
    public ulong MethodId { get; init; } = 0;

    /// <summary>
    /// The published native code version.
    /// </summary>
    public ulong? CodeVersion { get; init; }

    /// <summary>
    /// The published native code address.
    /// </summary>
    public ulong Address { get; init; } = 0;

    /// <summary>
    /// The original complete listing.
    /// </summary>
    public string Listing { get; init; } = "";

    /// <summary>
    /// The symbolized instruction listing.
    /// </summary>
    public string[] Normalized { get; init; } = [];

    /// <summary>
    /// The observed successful inline contributors.
    /// </summary>
    public string[] Inlinees { get; init; } = [];
}
