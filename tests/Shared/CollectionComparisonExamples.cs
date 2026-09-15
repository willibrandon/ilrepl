namespace IlRepl.Tests.Shared;

/// <summary>
/// Returns real collections with observable contents and framework string comparer settings.
/// </summary>
public static class CollectionComparisonExamples
{
    /// <summary>
    /// Constructs a dictionary or hash set without exposing its private randomized storage.
    /// </summary>
    /// <param name="set">Whether to return a hash set instead of a dictionary.</param>
    /// <param name="comparer">The StringComparer property, or default for the parameterless constructor.</param>
    /// <param name="edited">Whether to change one element or value.</param>
    /// <returns>The complete selected method declaration.</returns>
    public static string Method(bool set, string comparer, bool edited)
    {
        var collection = set ? "class HashSet<string>" : "class Dictionary<string, int32>";
        var constructor = comparer == "default" ? "newobj instance void " + collection + "::.ctor()\n"
            : "call class StringComparer StringComparer::get_" + comparer + "()\nnewobj instance void " + collection
                + "::.ctor(class IEqualityComparer<!0>)\n";
        return ".method public static " + collection + " Read() {\n"
            + constructor + "dup\nldstr \"first\"\n"
            + (set ? "callvirt instance bool " + collection + "::Add(!0)\npop\n"
                : "ldc.i4.s 42\ncallvirt instance void " + collection + "::Add(!0, !1)\n")
            + "dup\nldstr \"" + (set && edited ? "changed" : "second") + "\"\n"
            + (set ? "callvirt instance bool " + collection + "::Add(!0)\npop\n"
                : "ldc.i4.s " + (edited ? "44" : "43") + "\ncallvirt instance void " + collection + "::Add(!0, !1)\n")
            + "ret\n}";
    }
}
