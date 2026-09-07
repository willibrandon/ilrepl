namespace IlRepl.Engine;

/// <summary>
/// How the runtime lays out a type's instance fields.
/// </summary>
public enum TypeLayoutKind
{
    /// <summary>
    /// The runtime chooses; the default.
    /// </summary>
    Auto,

    /// <summary>
    /// Fields in declaration order, aligned by <c>.pack</c>.
    /// </summary>
    Sequential,

    /// <summary>
    /// Every instance field at the offset written before it.
    /// </summary>
    Explicit,
}
