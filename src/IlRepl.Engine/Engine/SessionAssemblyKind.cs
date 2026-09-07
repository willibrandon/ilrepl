namespace IlRepl.Engine;

/// <summary>
/// What a session-owned assembly holds.
/// </summary>
public enum SessionAssemblyKind
{
    /// <summary>
    /// A family of types defined with <c>.class</c>, one version.
    /// </summary>
    Types,

    /// <summary>
    /// One version of a method defined with <c>.method</c>.
    /// </summary>
    Methods,

    /// <summary>
    /// The stable entry point of a session method, which every caller binds to.
    /// </summary>
    Trampoline,

    /// <summary>
    /// A cell compiled for one run.
    /// </summary>
    Cell,
}
