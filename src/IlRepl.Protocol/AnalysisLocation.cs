namespace IlRepl.Protocol;

/// <summary>
/// Locates source in a body using zero-based lines and UTF-16 character offsets.
/// </summary>
/// <param name="Body">The body identity within the captured document.</param>
/// <param name="Line">The source line, or -1 for a synthetic boundary.</param>
/// <param name="Start">The first character on the line.</param>
/// <param name="Length">The number of characters.</param>
/// <param name="Offset">The IL byte offset when the source is disassembled.</param>
public sealed record AnalysisLocation(string Body, int Line, int Start, int Length, int? Offset = null);
