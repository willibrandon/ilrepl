namespace IlRepl.Tui;

/// <summary>
/// What a unit of a submission is.
/// </summary>
public enum SubmissionUnitKind
{
    /// <summary>
    /// One line at the top level.
    /// </summary>
    Line,

    /// <summary>
    /// A brace block: a method, a class, or a protected region with its handlers, from its opening line to the line that balances it.
    /// </summary>
    Block,

    /// <summary>
    /// A blank line at the top level, which runs the cell.
    /// </summary>
    Run,
}
