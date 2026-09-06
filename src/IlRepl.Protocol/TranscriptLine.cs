namespace IlRepl.Protocol;

/// <summary>
/// One line of the transcript: a kind and the styled spans that make it up.
/// </summary>
/// <param name="Kind">What the line represents.</param>
/// <param name="Spans">The styled text runs, left to right.</param>
public sealed record TranscriptLine(LineKind Kind, IReadOnlyList<TranscriptSpan> Spans)
{
    /// <summary>
    /// Creates a single-span line.
    /// </summary>
    /// <param name="kind">What the line represents.</param>
    /// <param name="text">The text.</param>
    /// <param name="style">The style.</param>
    /// <returns>The line.</returns>
    public static TranscriptLine Of(LineKind kind, string text, SpanStyle style = SpanStyle.Default) => new(kind, [new TranscriptSpan(text, style)]);

    /// <summary>
    /// The text of the line without styling.
    /// </summary>
    public string PlainText => string.Concat(Spans.Select(s => s.Text));
}
