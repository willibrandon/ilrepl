namespace IlRepl.Protocol;

/// <summary>
/// Describes whether an evaluation stack can be displayed at a source position.
/// </summary>
public enum AnalyzedStackKind
{
    /// <summary>
    /// The height is known, although individual values may have unknown types.
    /// </summary>
    Known,

    /// <summary>
    /// An unresolved effect prevents determining the complete stack.
    /// </summary>
    Unknown,

    /// <summary>
    /// No valid stack can reach this point because an established path is invalid.
    /// </summary>
    Invalid,

    /// <summary>
    /// No control-flow path reaches this point.
    /// </summary>
    Unreachable,
}
