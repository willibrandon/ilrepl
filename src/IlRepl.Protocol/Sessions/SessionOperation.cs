namespace IlRepl.Protocol;

/// <summary>
/// The workspace operations supported by the typed engine contract.
/// </summary>
public enum SessionOperation
{
    /// <summary>
    /// Describe the current document and runtime.
    /// </summary>
    Summary,

    /// <summary>
    /// Capture source with the matching editor revision.
    /// </summary>
    Capture,

    /// <summary>
    /// Acknowledges the exact revision written successfully by the host storage service.
    /// </summary>
    AcknowledgeSave,

    /// <summary>
    /// Publishes a verified dependency graph without replacing unaffected live runtime state.
    /// </summary>
    AdoptReferences,

    /// <summary>
    /// Write or download a session document.
    /// </summary>
    Save,

    /// <summary>
    /// Read and reconstruct a document in a replacement runtime.
    /// </summary>
    Open,

    /// <summary>
    /// Reconstruct the supplied document without execution.
    /// </summary>
    Hydrate,

    /// <summary>
    /// Recover locked external dependencies.
    /// </summary>
    Restore,

    /// <summary>
    /// List source submissions and historical results.
    /// </summary>
    Cells,

    /// <summary>
    /// Recall one numbered submission without execution.
    /// </summary>
    Cell,

    /// <summary>
    /// Explicitly replay the supplied experiment.
    /// </summary>
    Run,

    /// <summary>
    /// Load a project or package dependency.
    /// </summary>
    Load,

    /// <summary>
    /// Request a frontend-controlled session exit.
    /// </summary>
    Quit,

}
