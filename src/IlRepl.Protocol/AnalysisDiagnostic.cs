namespace IlRepl.Protocol;

/// <summary>
/// Describes an analysis finding and the source instructions that explain it.
/// </summary>
/// <param name="Code">A stable rule identifier.</param>
/// <param name="Kind">Whether the finding prevents execution.</param>
/// <param name="Message">The explanation.</param>
/// <param name="Location">The primary source location.</param>
/// <param name="Related">Incoming paths and value producers.</param>
public sealed record AnalysisDiagnostic(
    string Code,
    AnalysisDiagnosticKind Kind,
    string Message,
    AnalysisLocation Location,
    IReadOnlyList<AnalysisRelatedLocation> Related);
