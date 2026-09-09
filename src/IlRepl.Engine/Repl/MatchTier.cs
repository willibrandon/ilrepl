namespace IlRepl.Repl;

/// <summary>
/// Orders completion matches from exact names through progressively broader word matches.
/// </summary>
public enum MatchTier
{
    /// <summary>
    /// The whole name equals the query.
    /// </summary>
    Exact,
    /// <summary>
    /// The name starts with the query.
    /// </summary>
    Prefix,
    /// <summary>
    /// The query follows consecutive letters and word boundaries.
    /// </summary>
    Humps,
    /// <summary>
    /// The query occurs inside the name.
    /// </summary>
    Substring,
    /// <summary>
    /// The name does not match.
    /// </summary>
    None,
}
