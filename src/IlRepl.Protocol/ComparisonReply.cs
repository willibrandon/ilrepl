namespace IlRepl.Protocol;

/// <summary>
/// An execution comparison under explicit starting conditions and independently isolated runtimes.
/// </summary>
/// <param name="Name">The edit name.</param>
/// <param name="BaselineFingerprint">The immutable original identity.</param>
/// <param name="Revision">The committed revision that ran.</param>
/// <param name="Outcome">Match, different, different-inputs, or incomplete.</param>
/// <param name="StartingState">The declaration state, input, environment, and filesystem conditions.</param>
/// <param name="Original">The original-side result.</param>
/// <param name="Edited">The edited-side result.</param>
public sealed record ComparisonReply(string Name, string BaselineFingerprint, int Revision, string Outcome, string StartingState,
    ComparisonSide Original, ComparisonSide Edited);
