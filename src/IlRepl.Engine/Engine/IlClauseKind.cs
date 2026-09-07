namespace IlRepl.Engine;

/// <summary>
/// The kinds of exception handling clause a method body declares (ECMA-335 II.25.4.6).
/// </summary>
public enum IlClauseKind
{
    /// <summary>
    /// A typed catch handler.
    /// </summary>
    Catch,

    /// <summary>
    /// A filter followed by its handler.
    /// </summary>
    Filter,

    /// <summary>
    /// A finally handler.
    /// </summary>
    Finally,

    /// <summary>
    /// A fault handler.
    /// </summary>
    Fault,
}
