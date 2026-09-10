namespace IlRepl.Tui;

/// <summary>
/// Identifies the document, caret, and session captured for an analysis request.
/// </summary>
internal sealed record AnalysisRequestKey(string Text, long Version, int Line, int Caret, long Revision, long AssemblyVersion);
