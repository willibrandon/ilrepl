namespace IlRepl.Protocol;

/// <summary>
/// Requests stack analysis of an immutable document without submitting it.
/// </summary>
/// <param name="Lines">The entire unsent document.</param>
/// <param name="Line">The caret's zero-based line.</param>
/// <param name="Caret">The UTF-16 caret position on that line.</param>
/// <param name="DocumentVersion">The editor's document version.</param>
public sealed record AnalysisRequest(IReadOnlyList<string> Lines, int Line, int Caret, long DocumentVersion);
