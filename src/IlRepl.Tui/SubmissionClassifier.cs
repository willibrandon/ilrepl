using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Reads a reply against the mark its unit began with: a changed generation means the line ran,
/// committed, or discarded something, and nothing before it can be withdrawn any more.
/// </summary>
public static class SubmissionClassifier
{
    /// <summary>
    /// Classifies a reply.
    /// </summary>
    /// <param name="mark">The mark the unit began with.</param>
    /// <param name="reply">The engine's reply to one of the unit's lines.</param>
    /// <returns>What the reply means for the unit.</returns>
    public static SubmissionOutcome Classify(SessionMark mark, HandleReply reply)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentNullException.ThrowIfNull(reply);
        var changed = reply.Status.Mark.Generation != mark.Generation;
        return (changed, reply.Succeeded) switch
        {
            (false, true) => SubmissionOutcome.Accepted,
            (false, false) => SubmissionOutcome.Refused,
            (true, true) => SubmissionOutcome.Completed,
            (true, false) => SubmissionOutcome.Failed,
        };
    }
}
