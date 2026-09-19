namespace IlRepl.Protocol;

/// <summary>
/// Describes one stack operand and the instructions responsible for its value.
/// </summary>
/// <param name="Index">The bottom-based stack index, or -1 when the operand is missing.</param>
/// <param name="Role">The operand's role in the instruction.</param>
/// <param name="Expected">The requirement checked for this operand.</param>
/// <param name="Actual">The established type, or null for a missing operand.</param>
/// <param name="Producers">The distinct instructions that produced the value.</param>
public sealed record StackConflict(
    int Index,
    string Role,
    string Expected,
    string? Actual,
    IReadOnlyList<AnalysisSource> Producers);
