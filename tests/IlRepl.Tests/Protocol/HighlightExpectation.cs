using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// One caret annotation of a highlight fixture: the token that starts at a column must have a style.
/// </summary>
/// <param name="LineNumber">The line of the annotation in the fixture file.</param>
/// <param name="Column">The column the token starts at.</param>
/// <param name="Length">How many characters the token must cover, or zero for a <c>&lt;-</c> mark that checks the style alone.</param>
/// <param name="Style">The style.</param>
internal sealed record HighlightExpectation(int LineNumber, int Column, int Length, SpanStyle Style);
