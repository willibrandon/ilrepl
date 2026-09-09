using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Completion matching follows deterministic case, word-boundary and overload ordering rules.
/// </summary>
[TestClass]
public sealed class CandidateRankerTests
{
    /// <summary>
    /// The runner's cancellation and reporting context.
    /// </summary>
    public required TestContext TestContext { get; set; }

    /// <summary>
    /// Exact, prefix, hump and substring matches retain their distinct priorities and case preferences.
    /// </summary>
    [TestMethod]
    [DataRow("WriteLine", "WriteLine", MatchTier.Exact, true)]
    [DataRow("writeline", "WriteLine", MatchTier.Exact, false)]
    [DataRow("Wr", "WriteLine", MatchTier.Prefix, true)]
    [DataRow("wr", "WriteLine", MatchTier.Prefix, false)]
    [DataRow("WL", "WriteLine", MatchTier.Humps, true)]
    [DataRow("wl", "WriteLine", MatchTier.Humps, false)]
    [DataRow("ToS", "ToString", MatchTier.Prefix, true)]
    [DataRow("gC", "get_Count", MatchTier.Humps, true)]
    [DataRow("IOE", "IOException", MatchTier.Prefix, true)]
    [DataRow("IE", "IOException", MatchTier.Humps, true)]
    [DataRow("S2", "Stage2", MatchTier.Humps, true)]
    [DataRow("Line", "WriteLine", MatchTier.Substring, true)]
    [DataRow("line", "WriteLine", MatchTier.Substring, false)]
    [DataRow("wrl", "WriteLine", MatchTier.Humps, false)]
    [DataRow("WL", "ReadWriteLine", MatchTier.None, false)]
    [DataRow("", "Anything", MatchTier.Prefix, true)]
    public void Match_UsesTheStrongestTier(string query, string name, MatchTier tier, bool exactCase)
    {
        Assert.AreEqual(new CandidateMatch(tier, exactCase), CandidateRanker.Match(query, name));
    }

    /// <summary>
    /// Exact case precedes session preference within a tier, while exact names precede every prefix.
    /// </summary>
    [TestMethod]
    public void Rank_PreservesTierAndCasePriority()
    {
        CandidateRankFacts[] candidates = [new("writeLine", "writeLine", IsSession: true),
            new("WriteLine", "WriteLine"), new("WR", "WR"), new("ReadWr", "ReadWr"),
            new("Wrong", "Wrong", IsSession: true)];
        var ranked = CandidateRanker.Rank(candidates, "Wr", candidate => candidate, TestContext.CancellationToken);
        string[] expected = ["WR", "Wrong", "WriteLine", "writeLine", "ReadWr"];
        Assert.AreSequenceEqual(expected, ranked.Select(candidate => candidate.Label).ToArray());
    }

    /// <summary>
    /// Overloads with the same name sort by parameter count before their parameter spelling.
    /// </summary>
    [TestMethod]
    public void Rank_OrdersOverloadsAndKeepsTiesStable()
    {
        CandidateRankFacts[] candidates = [new("M", "M", ParameterCount: 1, ParameterList: "string"),
            new("M", "M", ParameterCount: 0), new("M", "M", ParameterCount: 1, ParameterList: "int32"),
            new("M", "M", ParameterCount: 1, ParameterList: "int32")];
        var ranked = CandidateRanker.Rank(candidates, "M", candidate => candidate, TestContext.CancellationToken);
        Assert.AreSame(candidates[1], ranked[0]);
        Assert.AreSame(candidates[2], ranked[1]);
        Assert.AreSame(candidates[3], ranked[2]);
        Assert.AreSame(candidates[0], ranked[3]);
    }
}
