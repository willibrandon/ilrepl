namespace IlRepl.Protocol;

/// <summary>
/// Presents the stack at a source position without exposing runtime type objects.
/// </summary>
/// <param name="Kind">Whether the stack is known, unknown, invalid, or unreachable.</param>
/// <param name="Values">Display names of the known stack slots, bottom first.</param>
/// <param name="Incomplete">Whether the enclosing body is still missing source.</param>
public sealed record AnalyzedStack(AnalyzedStackKind Kind, IReadOnlyList<string> Values, bool Incomplete = false)
{
    /// <summary>
    /// The known height, or null when the complete stack cannot be established.
    /// </summary>
    public int? Depth => Kind == AnalyzedStackKind.Known ? Values.Count : null;

    /// <summary>
    /// Formats the stack using the same states as instruction listings.
    /// </summary>
    /// <returns>The stack column.</returns>
    public string Render() => Kind switch
    {
        AnalyzedStackKind.Known => "[" + string.Join(", ", Values) + "]",
        AnalyzedStackKind.Unknown => "?",
        AnalyzedStackKind.Invalid => "invalid",
        _ => "unreachable",
    };
}
