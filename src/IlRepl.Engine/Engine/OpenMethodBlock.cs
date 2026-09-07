namespace IlRepl.Engine;

/// <summary>
/// The <c>.method</c> block being typed: its header, the lines so far, and the state they
/// produced. Nothing here is visible to calls from the cell until the block closes.
/// </summary>
internal sealed class OpenMethodBlock
{
    /// <summary>
    /// The <c>.method</c> line as typed.
    /// </summary>
    public required string HeaderLine { get; init; }

    /// <summary>
    /// The signature parsed from the header.
    /// </summary>
    public required MethodSignature Signature { get; init; }

    /// <summary>
    /// The method this block replaces when it closes, or null for a new name.
    /// </summary>
    public required SessionMethod? Replacing { get; init; }

    /// <summary>
    /// The method table as it will be once the block commits: the session's signatures with this
    /// one added or substituted, so the body can call itself.
    /// </summary>
    public required IReadOnlyList<MethodSignature> Signatures { get; init; }

    /// <summary>
    /// The validated body so far. Replaced on undo.
    /// </summary>
    public required CellState State { get; set; }

    /// <summary>
    /// The body lines so far, as typed.
    /// </summary>
    public List<string> BodyLines { get; } = [];
}
