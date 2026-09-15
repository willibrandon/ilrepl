using System.Globalization;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds real concurrent dictionaries with controlled logical entries and string comparers.
/// </summary>
public static class ConcurrentCollectionComparisonExamples
{
    private static readonly string[] Keys = ["zeta", "alpha", "omega", "beta", "theta", "eta", "delta", "gamma"];

    /// <summary>
    /// Returns expected logical entries independently of the dictionary's runtime storage order.
    /// </summary>
    /// <param name="count">The original number of entries, from zero through eight.</param>
    /// <param name="edited">Whether to increment the final value or add an entry to the empty dictionary.</param>
    /// <returns>The expected keys and values.</returns>
    public static IReadOnlyDictionary<string, int> Contents(int count, bool edited = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Keys.Length);
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);
        var length = edited && count == 0 ? 1 : count;
        for (var index = 0; index < length; index++)
            entries.Add(Keys[index], 42 + index + (edited && index == length - 1 ? 1 : 0));
        return entries;
    }

    /// <summary>
    /// Creates a method returning a genuine concurrent dictionary without depending on enumeration order.
    /// </summary>
    /// <param name="count">The original entry count.</param>
    /// <param name="comparer">The StringComparer property name, or default for the default constructor.</param>
    /// <param name="edited">Whether to change logical contents.</param>
    /// <param name="reverse">Whether to insert entries in reverse order.</param>
    /// <returns>The complete selected method declaration.</returns>
    public static string Method(int count, string comparer = "default", bool edited = false, bool reverse = false)
    {
        const string dictionary = "class ConcurrentDictionary<string, int32>";
        var source = ".method public static " + dictionary + " Read() {\n";
        source += comparer == "default" ? "newobj instance void " + dictionary + "::.ctor()\n"
            : "call class StringComparer StringComparer::get_" + comparer + "()\nnewobj instance void " + dictionary
                + "::.ctor(class IEqualityComparer<string>)\n";
        var entries = Contents(count, edited);
        foreach (var (key, value) in reverse ? entries.Reverse() : entries)
        {
            source += "dup\nldstr \"" + key + "\"\nldc.i4 " + value.ToString(CultureInfo.InvariantCulture)
                + "\ncallvirt instance bool " + dictionary + "::TryAdd(string, int32)\npop\n";
        }
        return source + "ret\n}";
    }
}
