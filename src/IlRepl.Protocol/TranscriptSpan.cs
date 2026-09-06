namespace IlRepl.Protocol;

/// <summary>
/// A run of transcript text with one style.
/// </summary>
/// <param name="Text">The text.</param>
/// <param name="Style">The style.</param>
public sealed record TranscriptSpan(string Text, SpanStyle Style = SpanStyle.Default);
