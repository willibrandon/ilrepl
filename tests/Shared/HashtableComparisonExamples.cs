using System.Globalization;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds real hashtables with string keys, explicit comparers, and synchronized wrappers.
/// </summary>
public static class HashtableComparisonExamples
{
    /// <summary>
    /// Returns independently named logical entries for the original or revised method.
    /// </summary>
    /// <param name="count">The original number of entries.</param>
    /// <param name="edited">Whether to increment the last value or add to an empty table.</param>
    /// <returns>The expected key/value mapping.</returns>
    public static IReadOnlyDictionary<string, int> Contents(int count, bool edited = false) =>
        ConcurrentCollectionComparisonExamples.Contents(count, edited);

    /// <summary>
    /// Creates a method returning a hashtable with controlled logical contents.
    /// </summary>
    /// <param name="count">The original entry count.</param>
    /// <param name="wrappers">The number of synchronized wrappers.</param>
    /// <param name="comparer">Default, legacy, or a StringComparer property name.</param>
    /// <param name="edited">Whether to change logical contents.</param>
    /// <param name="reverse">Whether to insert keys in reverse order.</param>
    /// <returns>The complete selected method declaration.</returns>
    public static string Method(int count, int wrappers = 0, string comparer = "default", bool edited = false, bool reverse = false)
    {
        var source = ".method public static class Hashtable Read() {\n";
        source += comparer switch
        {
            "default" => "newobj instance void Hashtable::.ctor()\n",
            "legacy" => "call class CaseInsensitiveHashCodeProvider CaseInsensitiveHashCodeProvider::get_DefaultInvariant()\n"
                + "call class CaseInsensitiveComparer CaseInsensitiveComparer::get_DefaultInvariant()\n"
                + "newobj instance void Hashtable::.ctor(class IHashCodeProvider, class IComparer)\n",
            _ => "call class StringComparer StringComparer::get_" + comparer
                + "()\nnewobj instance void Hashtable::.ctor(class IEqualityComparer)\n",
        };

        var entries = Contents(count, edited);
        foreach (var (key, value) in reverse ? entries.Reverse() : entries)
        {
            source += "dup\nldstr \"" + key + "\"\nldc.i4 " + value.ToString(CultureInfo.InvariantCulture)
                + "\nbox int32\ncallvirt instance void Hashtable::Add(object, object)\n";
        }

        for (var index = 0; index < wrappers; index++)
        {
            source += "call class Hashtable Hashtable::Synchronized(class Hashtable)\n";
        }

        return source + "ret\n}";
    }
}
