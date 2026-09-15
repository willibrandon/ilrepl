namespace IlRepl.Protocol;

/// <summary>
/// A captured dependency image loaded by identity in an isolated comparison runtime.
/// </summary>
/// <param name="Name">The full assembly identity.</param>
/// <param name="Image">The retained PE image.</param>
public sealed record ComparisonAssembly(string Name, byte[] Image)
{
    /// <summary>
    /// The original file path, verified before loading when a comparison needs the original assembly context.
    /// </summary>
    public string? OriginalLocation { get; init; }

    /// <summary>
    /// The adjacent satellite files available when the original assembly context was captured, including an empty inventory.
    /// </summary>
    public IReadOnlyList<string>? OriginalSatelliteFiles { get; init; }

    /// <summary>
    /// Whether the original assembly belongs to a collectible load context.
    /// </summary>
    public bool IsCollectible { get; init; }
}
