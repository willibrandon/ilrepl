namespace IlRepl.Tui;

/// <summary>
/// Identifies the permitted effect of one terminal interrupt key press.
/// </summary>
internal enum InterruptAction
{
    /// <summary>
    /// No execution or repeated-interrupt guard consumes this press.
    /// </summary>
    None,

    /// <summary>
    /// Consume the key while cancellation settles or before escalation is displayed.
    /// </summary>
    Consume,

    /// <summary>
    /// Request cooperative cancellation of the observed operation.
    /// </summary>
    Cancel,

    /// <summary>
    /// The user explicitly authorized replacement of the displayed operation's runtime.
    /// </summary>
    Restart,
}
