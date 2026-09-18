namespace IlRepl.Protocol;

/// <summary>
/// Identifies how an active operation can respond to an interruption request.
/// </summary>
public enum ExecutionPhase
{
    /// <summary>
    /// Engine-owned work observes cancellation and preserves the runtime.
    /// </summary>
    Cooperative,

    /// <summary>
    /// User code is running and may require explicit runtime replacement.
    /// </summary>
    UserCode,

    /// <summary>
    /// The operation is releasing resources after execution or cancellation.
    /// </summary>
    Cleanup,

    /// <summary>
    /// The operation has reported that it cannot complete cancellation.
    /// </summary>
    CannotStop,
}
