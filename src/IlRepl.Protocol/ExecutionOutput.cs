namespace IlRepl.Protocol;

/// <summary>
/// Carries a sequenced portion of console output before execution has returned.
/// </summary>
/// <param name="Identity">The execution operation that produced the text.</param>
/// <param name="Sequence">The increasing output sequence within the operation.</param>
/// <param name="Text">The exact console text, including any partial line.</param>
/// <param name="IsError">Whether the text was written to standard error.</param>
public sealed record ExecutionOutput(string Identity, long Sequence, string Text, bool IsError)
{
    /// <summary>
    /// The actual input echoes and transcript records that must appear before this console chunk.
    /// </summary>
    public IReadOnlyList<TranscriptLine> LeadingLines { get; init; } = [];
}
