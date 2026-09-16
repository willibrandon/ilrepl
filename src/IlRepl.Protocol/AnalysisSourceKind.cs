namespace IlRepl.Protocol;

/// <summary>
/// Distinguishes editable, accepted, imported, implicit, and unlocated value producers.
/// </summary>
public enum AnalysisSourceKind
{
    /// <summary>
    /// Source in the analyzed editor document.
    /// </summary>
    Document,

    /// <summary>
    /// Source already accepted into the session.
    /// </summary>
    Accepted,

    /// <summary>
    /// An instruction in an imported method body.
    /// </summary>
    Imported,

    /// <summary>
    /// An implicit value, such as the exception supplied to a handler.
    /// </summary>
    Synthetic,

    /// <summary>
    /// An instruction submitted without source coordinates.
    /// </summary>
    Unavailable,
}
