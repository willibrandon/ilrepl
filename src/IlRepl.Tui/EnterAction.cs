namespace IlRepl.Tui;

/// <summary>
/// What Enter will do now. The key handler and the status bar's hint both read it, so the hint
/// never promises one thing while the key does another.
/// </summary>
public enum EnterAction
{
    /// <summary>
    /// Send the buffer to the engine.
    /// </summary>
    Submit,

    /// <summary>
    /// Open a new line under the caret: a brace, a comment, or a string is still open.
    /// </summary>
    Continue,

    /// <summary>
    /// Put the completion the user moved to into the buffer, and send nothing.
    /// </summary>
    AcceptCompletion,

    /// <summary>
    /// Lines are in flight: the bar offers cancel, and what Enter sends waits its turn.
    /// </summary>
    Busy,
}
