using System.Text;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds actual immutable hash collections with independent key and value comparer settings.
/// </summary>
public static class ImmutableCollectionComparisonExamples
{
    private static readonly string[] Keys = ["zeta", "alpha", "omega", "beta", "theta", "eta", "delta", "gamma"];

    /// <summary>
    /// Returns the logical contents independently of the runtime's hash-dependent enumeration order.
    /// </summary>
    /// <param name="count">The original entry count, from zero through eight.</param>
    /// <param name="edited">Whether to change the last entry, or add an entry to an empty collection.</param>
    /// <param name="set">Whether the keys themselves are the elements being edited.</param>
    /// <returns>The expected logical keys and dictionary values.</returns>
    public static IReadOnlyDictionary<string, string> Contents(int count, bool edited, bool set)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Keys.Length);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var length = count == 0 && edited ? 1 : count;
        for (var index = 0; index < length; index++)
        {
            var changed = edited && index == length - 1;
            entries.Add(set && changed ? "changed" : Keys[index], changed ? "changed-value" : "value-" + index);
        }

        return entries;
    }

    /// <summary>
    /// Produces a complete IL method returning a persistent immutable collection.
    /// </summary>
    /// <param name="set">Whether to build a set instead of a dictionary.</param>
    /// <param name="count">The original number of elements.</param>
    /// <param name="keyComparer">The StringComparer property, or default.</param>
    /// <param name="valueComparer">The dictionary value comparer property, or default.</param>
    /// <param name="edited">Whether to change logical contents while retaining comparer settings.</param>
    /// <returns>The selected method declaration.</returns>
    public static string Method(
        bool set,
        int count,
        string keyComparer = "default",
        string valueComparer = "default",
        bool edited = false)
    {
        var collection = set ? "class ImmutableHashSet<string>" : "class ImmutableDictionary<string, string>";
        var source = new StringBuilder(".method public static " + collection + " Read() {\nldsfld " + collection + " " + collection
            + "::Empty\n");
        source.Append(Comparer(keyComparer));
        if (!set)
        {
            source.Append(Comparer(valueComparer));
        }

        source.Append("callvirt instance " + collection + " " + collection + (set
            ? "::WithComparer(class IEqualityComparer<string>)\n"
            : "::WithComparers(class IEqualityComparer<string>, class IEqualityComparer<string>)\n"));
        foreach (var (key, value) in Contents(count, edited, set))
        {
            source.Append("ldstr \"" + key + "\"\n");
            if (!set)
            {
                source.Append("ldstr \"" + value + "\"\n");
            }

            source.Append("callvirt instance " + collection + " " + collection + (set ? "::Add(string)\n" : "::Add(string, string)\n"));
        }

        return source.Append("ret\n}").ToString();
    }

    private static string Comparer(string name) => name == "default" ? "ldnull\n"
        : "call class StringComparer StringComparer::get_" + name + "()\n";
}
