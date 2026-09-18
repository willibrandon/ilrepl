namespace IlRepl.Protocol;

/// <summary>
/// Describes execution availability independently of the editable source workspace.
/// </summary>
public enum SessionRuntimeState
{
    /// <summary>
    /// The host is accepting execution requests.
    /// </summary>
    Ready,

    /// <summary>
    /// The initial host connection is being established.
    /// </summary>
    Starting,

    /// <summary>
    /// The previous runtime is stopping and acknowledged source is being reconstructed.
    /// </summary>
    Restarting,

    /// <summary>
    /// Execution is unavailable while source editing, saving, and restart remain accessible.
    /// </summary>
    Unavailable,
}
