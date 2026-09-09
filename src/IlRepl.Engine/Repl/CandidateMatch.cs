namespace IlRepl.Repl;

/// <summary>
/// Retains a candidate's matching tier and whether the matched characters have exactly the typed case.
/// </summary>
/// <param name="Tier">The strongest matching tier.</param>
/// <param name="ExactCase">Whether the matched characters preserve the query's case.</param>
public readonly record struct CandidateMatch(MatchTier Tier, bool ExactCase);
