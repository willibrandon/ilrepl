namespace IlRepl.Protocol;

/// <summary>
/// The user's choice when a document operation would discard unsaved source.
/// </summary>
public enum SessionDecision
{
    /// <summary>
    /// Preserve the current workspace and cancel the requested operation.
    /// </summary>
    Cancel,

    /// <summary>
    /// Save the current source before continuing.
    /// </summary>
    Save,

    /// <summary>
    /// Continue without saving the current source.
    /// </summary>
    Discard,
}
