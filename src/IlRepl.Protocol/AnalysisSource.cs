namespace IlRepl.Protocol;

/// <summary>
/// Retains a producer's source independently of the editor's current text.
/// </summary>
/// <param name="Location">The source location, including its body identity.</param>
/// <param name="Source">The instruction or implicit-entry description.</param>
/// <param name="Kind">Whether the source belongs to the current document.</param>
public sealed record AnalysisSource(AnalysisLocation Location, string Source, AnalysisSourceKind Kind);
