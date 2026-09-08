namespace IlRepl.Tests.Protocol;

/// <summary>
/// One subject line of a highlight fixture with the annotations under it.
/// </summary>
/// <param name="LineNumber">The line of the subject in the fixture file.</param>
/// <param name="Subject">The line to tokenize.</param>
/// <param name="Expectations">The annotations, in order.</param>
internal sealed record HighlightCase(int LineNumber, string Subject, IReadOnlyList<HighlightExpectation> Expectations);
