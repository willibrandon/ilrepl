namespace IlRepl.Protocol;

/// <summary>
/// The instruction-level normalized view and its explicit unresolved-address evidence.
/// </summary>
public sealed record NativeNormalization
{
    /// <summary>
    /// The original instruction structure with only proven addresses symbolized.
    /// </summary>
    public string[] Lines { get; init; } = [];

    /// <summary>
    /// Address evidence extended by compilation-only probes.
    /// </summary>
    public NativeAddressFact[] Addresses { get; init; } = [];

    /// <summary>
    /// Missing evidence that prevents a trustworthy normal comparison.
    /// </summary>
    public string[] Problems { get; init; } = [];
}
