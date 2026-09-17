namespace IlRepl.Protocol;

/// <summary>
/// Identifies a compilation-only address probe and the metadata operand it proves.
/// </summary>
public sealed record NativeProbe
{
    /// <summary>
    /// The unique method name in the probe listing.
    /// </summary>
    public string Method { get; init; } = "";

    /// <summary>
    /// The original operand role.
    /// </summary>
    public string Kind { get; init; } = "";

    /// <summary>
    /// The complete stable operand identity.
    /// </summary>
    public string Symbol { get; init; } = "";

    /// <summary>
    /// The concise CIL operand associated with the full stable identity.
    /// </summary>
    public string DisplaySymbol { get; init; } = "";
}
