namespace IlRepl.Repl;

/// <summary>
/// Matches names case-insensitively and orders completion candidates independently of insertion confirmation.
/// </summary>
public static class CandidateRanker
{
    /// <summary>
    /// Classifies the strongest exact, prefix, word-boundary or substring match.
    /// </summary>
    /// <param name="query">The decoded text before the caret.</param>
    /// <param name="name">The candidate's decoded matching name.</param>
    /// <returns>The matching tier and case preference.</returns>
    public static CandidateMatch Match(string query, string name)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(name);
        if (query.Length == 0)
        {
            return new CandidateMatch(MatchTier.Prefix, true);
        }

        if (string.Equals(query, name, StringComparison.OrdinalIgnoreCase))
        {
            return new CandidateMatch(MatchTier.Exact, query == name);
        }

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return new CandidateMatch(MatchTier.Prefix, name.StartsWith(query, StringComparison.Ordinal));
        }

        if (Humps(query, name) is { } exactCase)
        {
            return new CandidateMatch(MatchTier.Humps, exactCase);
        }

        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return new CandidateMatch(MatchTier.Substring, name.Contains(query, StringComparison.Ordinal));
        }

        return new CandidateMatch(MatchTier.None, false);
    }

    /// <summary>
    /// Orders every matching candidate while preserving input order for otherwise identical facts.
    /// </summary>
    /// <typeparam name="T">The retained candidate payload.</typeparam>
    /// <param name="candidates">All candidates for the immutable query.</param>
    /// <param name="query">The decoded query.</param>
    /// <param name="facts">The candidate's presentation and ordering facts.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The complete stable order, before candidate confirmation and paging.</returns>
    public static IReadOnlyList<T> Rank<T>(
        IEnumerable<T> candidates, string query, Func<T, CandidateRankFacts> facts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(query);
        return candidates.Select(candidate =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = facts(candidate);
                return (Candidate: candidate, Facts: key, Match: Match(query, key.Name));
            })
            .Where(candidate => candidate.Match.Tier != MatchTier.None)
            .OrderBy(candidate => candidate.Match.Tier)
            .ThenByDescending(candidate => candidate.Match.ExactCase)
            .ThenByDescending(candidate => candidate.Facts.IsSession)
            .ThenBy(candidate => candidate.Facts.KindPreference)
            .ThenBy(candidate => candidate.Facts.IsCompilerGenerated)
            .ThenBy(candidate => candidate.Facts.Label.Length)
            .ThenBy(candidate => candidate.Facts.Label, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Facts.ParameterCount)
            .ThenBy(candidate => candidate.Facts.ParameterList, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Facts.InheritanceDepth)
            .ThenBy(candidate => candidate.Facts.DeclaringPath, StringComparer.Ordinal)
            .Select(candidate => candidate.Candidate).ToArray();
    }

    private static bool? Humps(string query, string name)
    {
        if (name.Length == 0 || !SameLetter(query[0], name[0]))
        {
            return null;
        }

        var position = 0;
        var exact = query[0] == name[0];
        for (var i = 1; i < query.Length; i++)
        {
            var next = position + 1;
            if (next >= name.Length)
            {
                return null;
            }

            if (!SameLetter(query[i], name[next]))
            {
                while (next < name.Length && (!IsHump(name, next) || !SameLetter(query[i], name[next])))
                {
                    next++;
                }
            }

            if (next == name.Length)
            {
                return null;
            }

            exact &= query[i] == name[next];
            position = next;
        }

        return exact;
    }

    private static bool SameLetter(char left, char right) => char.ToUpperInvariant(left) == char.ToUpperInvariant(right);

    private static bool IsHump(string name, int position)
    {
        var current = name[position];
        var previous = name[position - 1];
        return char.IsUpper(current) && (!char.IsUpper(previous)
                || position + 1 < name.Length && char.IsLower(name[position + 1]))
            || char.IsLetterOrDigit(current) && previous is '_' or '.' or '/' or '<' or '>' or '`'
            || char.IsDigit(current) && !char.IsDigit(previous);
    }
}
