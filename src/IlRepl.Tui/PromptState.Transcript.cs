namespace IlRepl.Tui;

/// <summary>
/// Retains transcript presentation independently of changing input, caret, and completion state.
/// </summary>
public sealed partial class PromptState
{
    /// <summary>
    /// The retained widgets and folded rows for this prompt's transcript.
    /// </summary>
    internal TranscriptViewCache TranscriptView { get; } = new();
}
