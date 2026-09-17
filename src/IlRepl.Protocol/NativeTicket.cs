namespace IlRepl.Protocol;

/// <summary>
/// A one-use native request awaiting execution after its settings have been displayed.
/// </summary>
public sealed record NativeTicket
{
    /// <summary>
    /// The captured request identity.
    /// </summary>
    public string Identity { get; init; } = "";

    /// <summary>
    /// The selected target description.
    /// </summary>
    public string Name { get; init; } = "";
}
