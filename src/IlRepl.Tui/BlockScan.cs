namespace IlRepl.Tui;

/// <summary>
/// What a scan of a buffer's braces found.
/// </summary>
/// <param name="Depth">Opening braces minus closing braces, counted outside strings, quoted names, and comments, starting from the engine's open depth.</param>
/// <param name="InBlockComment">Whether a <c>/*</c> is still open at the end.</param>
/// <param name="AwaitingBrace">True when the last declaration header had no opening brace on its line: the
/// brace is counted as open already, and the next <c>{</c> is the header's rather than a new level.</param>
/// <param name="InString">Whether a string or quoted name is still open on the last line.</param>
public readonly record struct BlockScan(int Depth, bool InBlockComment, bool InString, bool AwaitingBrace = false);
