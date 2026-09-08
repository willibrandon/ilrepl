namespace IlRepl.Tui;

/// <summary>
/// What a reply to one sent line means for the unit it belongs to.
/// </summary>
public enum SubmissionOutcome
{
    /// <summary>
    /// The line was accepted provisionally; the unit goes on.
    /// </summary>
    Accepted,

    /// <summary>
    /// The line was refused before anything changed; the unit is rolled back and returned to the editor.
    /// </summary>
    Refused,

    /// <summary>
    /// The line completed something irreversible, a run or a commit; the unit boundary moves past it.
    /// </summary>
    Completed,

    /// <summary>
    /// The line failed after something irreversible happened, such as a cell that threw; the
    /// submission stops and only the unsent lines return to the editor.
    /// </summary>
    Failed,
}
