namespace IlRepl.Engine;

/// <summary>
/// Describes the possible initialization states of a reference-type constructor receiver.
/// </summary>
internal enum ConstructorThisState
{
    /// <summary>
    /// The enclosing body does not track constructor receiver initialization.
    /// </summary>
    NotTracked,

    /// <summary>
    /// The receiver is uninitialized on every incoming path.
    /// </summary>
    Uninitialized,

    /// <summary>
    /// The receiver is initialized on every incoming path.
    /// </summary>
    Initialized,

    /// <summary>
    /// The receiver is initialized on some incoming paths and uninitialized on others.
    /// </summary>
    Mixed,
}
