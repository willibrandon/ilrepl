namespace IlRepl.Tui;

/// <summary>
/// Identifies the document, caret, and session captured for an analysis request.
/// </summary>
internal sealed record AnalysisRequestKey(string Text, long Version, int Line, int Caret, long Revision, long AssemblyVersion)
{
    /// <summary>
    /// Compares source and semantic identity independently of a caret-only move.
    /// </summary>
    internal bool SameDocument(AnalysisRequestKey other) => Text == other.Text && Version == other.Version
        && Revision == other.Revision && AssemblyVersion == other.AssemblyVersion;
}
