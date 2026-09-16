namespace IlRepl.Protocol;

/// <summary>
/// The safe reconstruction operations represented in a session document.
/// </summary>
public enum SessionEntryKind
{
    /// <summary>
    /// Accept IL source without interpreting REPL commands.
    /// </summary>
    Source,

    /// <summary>
    /// Record an explicit executable cell boundary.
    /// </summary>
    Run,

    /// <summary>
    /// Abandon the current cell or open block.
    /// </summary>
    Clear,

    /// <summary>
    /// Clear definitions while retaining references and history.
    /// </summary>
    Reset,

    /// <summary>
    /// Withdraw the last accepted source item.
    /// </summary>
    Undo,

    /// <summary>
    /// Restore a provisional source mark.
    /// </summary>
    Rollback,

    /// <summary>
    /// Capture or reopen a method edit.
    /// </summary>
    Edit,

    /// <summary>
    /// Accept a physical line inside an edit block.
    /// </summary>
    EditSource,

    /// <summary>
    /// Select the recorded dependency revision.
    /// </summary>
    Reference,

    /// <summary>
    /// Retain rejected input without reconstructing it.
    /// </summary>
    Rejected,

}
