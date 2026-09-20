using System.Globalization;
using System.Text;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds real frozen dictionaries and sets with controlled contents and comparer settings.
/// </summary>
public static class FrozenCollectionComparisonExamples
{
    /// <summary>
    /// Returns logical contents independently of the factory's optimized representation or enumeration order.
    /// </summary>
    /// <param name="set">Whether edited keys themselves are the returned set elements.</param>
    /// <param name="count">The original entry count from zero through eight.</param>
    /// <param name="edited">Whether to change the last entry or add one to an empty collection.</param>
    /// <returns>The exact expected keys and dictionary values.</returns>
    public static IReadOnlyDictionary<string, int> Contents(bool set, int count, bool edited = false)
    {
        var entries = ConcurrentCollectionComparisonExamples.Contents(count, edited);
        return entries.Select((pair, index) => new KeyValuePair<string, int>(
            set && edited && index == entries.Count - 1 ? "changed" : pair.Key, pair.Value)).ToDictionary();
    }

    /// <summary>
    /// Produces a method using the actual framework frozen factory over a populated ordinary collection.
    /// </summary>
    /// <param name="set">Whether to return a set instead of a dictionary.</param>
    /// <param name="count">The original entry count.</param>
    /// <param name="comparer">The StringComparer property, or default.</param>
    /// <param name="edited">Whether to change logical contents.</param>
    /// <param name="reverse">Whether to populate the factory input in reverse order.</param>
    /// <returns>The complete selected method declaration.</returns>
    public static string Method(bool set, int count, string comparer = "default", bool edited = false, bool reverse = false)
    {
        var frozen = Type(set);
        var mutable = set ? "class HashSet<string>" : "class Dictionary<string, int32>";
        var source = new StringBuilder(".method public static " + frozen + " Read() {\nnewobj instance void " + mutable + "::.ctor()\n");
        var entries = Contents(set, count, edited);
        foreach (var (key, value) in reverse ? entries.Reverse() : entries)
        {
            source.Append("dup\nldstr \"" + key + "\"\n");
            if (!set)
            {
                source.Append("ldc.i4 " + value.ToString(CultureInfo.InvariantCulture) + "\n");
            }

            source.Append("callvirt instance " + (set ? "bool " : "void ") + mutable
                + (set ? "::Add(string)\npop\n" : "::Add(string, int32)\n"));
        }

        source.Append(comparer == "default" ? "ldnull\n" : "call class StringComparer StringComparer::get_" + comparer + "()\n");
        source.Append("call " + frozen + " System.Collections.Frozen." + (set ? "FrozenSet::ToFrozenSet<string>"
            : "FrozenDictionary::ToFrozenDictionary<string, int32>") + "(class IEnumerable<"
            + (set ? "string" : "valuetype KeyValuePair<string, int32>") + ">, class IEqualityComparer<string>)\n");
        return source.Append("ret\n}").ToString();
    }

    /// <summary>
    /// Returns the public frozen base type used by selected method signatures and real runtime assertions.
    /// </summary>
    /// <param name="set">Whether to return the set signature.</param>
    /// <returns>The IL collection type signature.</returns>
    public static string Type(bool set) => "class System.Collections.Frozen."
        + (set ? "FrozenSet<string>" : "FrozenDictionary<string, int32>");
}
