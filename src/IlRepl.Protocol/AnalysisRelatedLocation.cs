namespace IlRepl.Protocol;

/// <summary>
/// Explains an incoming path or value at another source location.
/// </summary>
/// <param name="Location">The related source.</param>
/// <param name="Message">Its contribution to the diagnostic.</param>
public sealed record AnalysisRelatedLocation(AnalysisLocation Location, string Message);
