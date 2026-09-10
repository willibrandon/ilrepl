namespace IlRepl.Protocol;

/// <summary>
/// An immutable document query; completion never submits its lines.
/// </summary>
/// <param name="Lines">The entire unsent document, including text after the caret.</param>
/// <param name="Line">The caret's zero-based line.</param>
/// <param name="Caret">The UTF-16 offset in that line.</param>
/// <param name="Cursor">The host's next-page token, or null for a new query.</param>
/// <param name="Anchors">The generic definitions selected in this document.</param>
/// <param name="Explicit">Whether the query was explicitly requested; retained through its pages.</param>
public sealed record CompletionRequest(
    IReadOnlyList<string> Lines,
    int Line,
    int Caret,
    string? Cursor,
    IReadOnlyList<ContinuationAnchor> Anchors,
    bool Explicit = false);
