namespace IlRepl.Repl;

/// <summary>
/// Supplies deterministic completion ordering facts without binding or spelling an insertion.
/// </summary>
/// <param name="Name">The decoded name or qualified path to match.</param>
/// <param name="Label">The complete label used to break ties.</param>
/// <param name="IsSession">Whether the declaration belongs to this session.</param>
/// <param name="KindPreference">The site's kind preference, with zero preferred.</param>
/// <param name="IsCompilerGenerated">Whether ordinary declarations should precede this candidate.</param>
/// <param name="ParameterCount">The number of fixed method parameters.</param>
/// <param name="ParameterList">The display spelling used to order overloads.</param>
/// <param name="InheritanceDepth">The distance from the requested type to the member's declaring type.</param>
/// <param name="DeclaringPath">The declaring type's full IL path.</param>
public sealed record CandidateRankFacts(
    string Name,
    string Label,
    bool IsSession = false,
    int KindPreference = 0,
    bool IsCompilerGenerated = false,
    int ParameterCount = 0,
    string ParameterList = "",
    int InheritanceDepth = 0,
    string DeclaringPath = "");
