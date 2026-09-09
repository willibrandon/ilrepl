using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Finds nearby accessible names for typo diagnostics using bounded edit distance.
/// </summary>
/// <remarks>
/// The did-you-mean a mistyped name gets: the nearest name by edit distance among the names the
/// context could have meant. Completion never uses edit distance and a suggestion never uses
/// completion's matching, so the two stay apart with their own thresholds.
/// </remarks>
public static class NameSuggestions
{
    /// <summary>
    /// Chooses the nearest name within the configured distance, preserving pool order for ties.
    /// </summary>
    /// <remarks>
    /// The nearest name in a pool, or null when nothing is near: a difference in case alone
    /// scores 0, anything else its Levenshtein distance, and only a distance under 3 counts. Ties
    /// keep the pool's order, so the answer is the same every time.
    /// </remarks>
    /// <param name="typo">The name as typed.</param>
    /// <param name="pool">The names that could have been meant, in a deterministic order.</param>
    /// <param name="accepts">An optional binding check performed before a name can become the nearest suggestion.</param>
    /// <returns>The suggestion, or null.</returns>
    public static string? Nearest(string typo, IEnumerable<string> pool, Func<string, bool>? accepts = null)
    {
        ArgumentNullException.ThrowIfNull(typo);
        ArgumentNullException.ThrowIfNull(pool);
        string? best = null;
        var bestDistance = 3;
        foreach (var name in pool)
        {
            if (name == typo)
            {
                continue;
            }

            var distance = string.Equals(name, typo, StringComparison.OrdinalIgnoreCase) ? 0 : EditDistance.WithinBound(typo, name,
                bestDistance - 1) ? EditDistance.Levenshtein(typo, name) : int.MaxValue;
            if (distance < bestDistance && (accepts is null || accepts(name)))
            {
                bestDistance = distance;
                best = name;
            }
        }

        return best;
    }

    /// <summary>
    /// The parenthetical a message carries a suggestion in.
    /// </summary>
    /// <param name="suggestion">The suggestion.</param>
    /// <returns>The text, with its leading space.</returns>
    public static string Parenthetical(string suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        return $" (did you mean '{suggestion}'?)";
    }

    /// <summary>
    /// Finds the nearest accessible type and returns the shortest spelling that binds to it.
    /// </summary>
    /// <remarks>
    /// The nearest type to a mistyped name among the session's types and the loaded assemblies,
    /// spelled the shortest way that binds to it: a short name when it is unique, else its
    /// qualified path. Nothing prefilters by first letter, so a wrong first letter is still found.
    /// The pool is bounded by length, pruned by a banded distance, and filtered to the types the
    /// context can mention.
    /// </remarks>
    /// <param name="ilName">The name as written, arity suffix included.</param>
    /// <param name="assemblyHint">The assembly named in square brackets, or null.</param>
    /// <param name="index">The index of every type the snapshot can see.</param>
    /// <param name="where">The context the type would be used from.</param>
    /// <param name="scope">The scope that binds the spelling.</param>
    /// <returns>The suggestion, or null.</returns>
    public static TypeSuggestion? NearestType(string ilName, string? assemblyHint, TypeIndex index, AccessContext where,
        IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(ilName);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(scope);
        var separator = Math.Max(ilName.LastIndexOf('.'), ilName.LastIndexOf('/'));
        var simple = separator < 0 ? ilName : ilName[(separator + 1)..];
        if (simple.Length == 0)
        {
            return null;
        }

        var bound = Math.Min(2, (simple.Length + 2) / 3);
        var confirmation = scope.ForSuggestions(out var lease);
        using var ownedLease = lease;
        var speller = new TypeSpeller((SnapshotBindingScope)confirmation);
        var facts = AccessFacts.From(confirmation);
        IEnumerable<TypeIndexEntry> candidates = index.Entries;
        if (assemblyHint is not null)
        {
            var hinted = index.Entries.Where(e => string.Equals(e.AssemblyName, assemblyHint, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hinted.Count > 0)
            {
                candidates = hinted;
            }
        }

        TypeIndexEntry? best = null;
        string? bestSpelling = null;
        var bestKey = (Distance: int.MaxValue, Common: 1, Session: 1, Path: "");
        foreach (var entry in candidates)
        {
            if (Math.Abs(entry.Name.Length - simple.Length) > bound || !EditDistance.WithinBound(simple, entry.Name, bound))
            {
                continue;
            }

            var distance = string.Equals(entry.Name, simple, StringComparison.OrdinalIgnoreCase) ? 0 : EditDistance.Levenshtein(simple,
                entry.Name);
            if (distance >= 3 || distance == 0 && entry.Name == simple)
            {
                continue;
            }

            var key = (Distance: distance, Common: TypeResolver.CommonNamespaces.Contains(entry.Namespace) ? 0 : 1,
                Session: entry.IsSession ? 0 : 1, Path: entry.IlPath);
            if (best is null || Compare(key, bestKey) < 0)
            {
                try
                {
                    var symbol = index.SymbolOf(entry);
                    if (symbol is null || MemberEligibility.AccessProblem(symbol, where, facts) is not null
                        || speller.TrySpell(symbol) is not { } spelling)
                    {
                        continue;
                    }

                    best = entry;
                    bestSpelling = spelling;
                    bestKey = key;
                }
                catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
                {
                    // A damaged candidate must not prevent another nearby name from being considered.
                }
            }
        }

        if (best is null)
        {
            return null;
        }

        return new TypeSuggestion(best, bestSpelling!);
    }

    private static int Compare((int Distance, int Common, int Session, string Path) a, (int Distance, int Common, int Session,
        string Path) b)
    {
        var byDistance = a.Distance.CompareTo(b.Distance);
        if (byDistance != 0)
        {
            return byDistance;
        }

        var byCommon = a.Common.CompareTo(b.Common);
        if (byCommon != 0)
        {
            return byCommon;
        }

        var bySession = a.Session.CompareTo(b.Session);
        return bySession != 0 ? bySession : string.CompareOrdinal(a.Path, b.Path);
    }

    /// <summary>
    /// Finds the shortest unambiguous spelling that binds to the requested type.
    /// </summary>
    /// <remarks>
    /// The shortest spelling of a type that binds to it in the scope: its short name, its
    /// qualified path, or its assembly-qualified path.
    /// </remarks>
    /// <param name="entry">The index entry.</param>
    /// <param name="target">The type.</param>
    /// <param name="index">The index.</param>
    /// <param name="scope">The scope that binds the spelling.</param>
    /// <returns>The spelling.</returns>
    public static string Spell(TypeIndexEntry entry, TypeSymbol target, TypeIndex index, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(scope);
        var confirmation = scope.ForSuggestions(out var lease);
        using var ownedLease = lease;
        return new TypeSpeller((SnapshotBindingScope)confirmation).Spell(target);
    }
}
