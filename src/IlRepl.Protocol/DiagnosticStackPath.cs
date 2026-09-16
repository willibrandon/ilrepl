namespace IlRepl.Protocol;

/// <summary>
/// Preserves one predecessor's stack separately from the other paths at a join.
/// </summary>
/// <param name="Source">The predecessor instruction or entry.</param>
/// <param name="Stack">The stack supplied by this path.</param>
/// <param name="Values">The path's stack slots and producers.</param>
public sealed record DiagnosticStackPath(AnalysisSource Source, AnalyzedStack Stack, IReadOnlyList<StackConflict> Values);
