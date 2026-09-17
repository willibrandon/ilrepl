namespace IlRepl.Protocol;

/// <summary>
/// A native address with a proven symbolic interpretation.
/// </summary>
public sealed record NativeAddressFact
{
    /// <summary>
    /// The observed process address.
    /// </summary>
    public ulong Address { get; init; } = 0;

    /// <summary>
    /// The address range length.
    /// </summary>
    public ulong Length { get; init; } = 1;

    /// <summary>
    /// The semantic role of the address.
    /// </summary>
    public string Kind { get; init; } = "";

    /// <summary>
    /// The stable identity used in normal comparisons.
    /// </summary>
    public string Symbol { get; init; } = "";

    /// <summary>
    /// The concise CIL operand used only for display, without changing the full comparison identity.
    /// </summary>
    public string DisplaySymbol { get; init; } = "";

    /// <summary>
    /// The metadata, event, annotation, or compilation probe establishing the identity.
    /// </summary>
    public string Evidence { get; init; } = "";
}
