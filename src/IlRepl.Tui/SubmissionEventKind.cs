namespace IlRepl.Tui;

/// <summary>
/// What an event posted to the prompt's queue is. The submission worker and the paste handler
/// post from other threads; the frame drains the queue on the render thread, the only thread
/// that touches the transcript and the prompt.
/// </summary>
public enum SubmissionEventKind
{
    /// <summary>
    /// A paste arrived; the text is the payload.
    /// </summary>
    Paste,

    /// <summary>
    /// The engine answered a line; the lines go to the transcript.
    /// </summary>
    Lines,

    /// <summary>
    /// A line was refused and its unit withdrawn; the text comes back to the editor with the refused line selected.
    /// </summary>
    Refused,

    /// <summary>
    /// The engine could not be reached, or a line failed after something ran; the unsent text comes back.
    /// </summary>
    Failed,

    /// <summary>
    /// The user cancelled; the text still to send comes back.
    /// </summary>
    Cancelled,

    /// <summary>
    /// A line asked to leave.
    /// </summary>
    Quit,

    /// <summary>
    /// Every line was sent.
    /// </summary>
    Completed,

    /// <summary>
    /// The history store finished loading; the entries replace what the prompt knew.
    /// </summary>
    HistoryLoaded,

    /// <summary>
    /// A completion request settled and its query identity must be checked before showing any rows.
    /// </summary>
    Completions,
}
