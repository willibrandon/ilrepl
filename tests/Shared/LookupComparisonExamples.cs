namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds LINQ lookups from session IL for desktop and browser comparisons.
/// </summary>
public static class LookupComparisonExamples
{
    /// <summary>
    /// Selects stable string keys without relying on compiler-generated delegates.
    /// </summary>
    public const string KeyMethod = ".method public static string Key(int32 value) {\n"
        + "ldarg.0\nldc.i4.2\nrem\nbrtrue.s second\nldstr \"first\"\nret\n"
        + "second:\nldstr \"second\"\nret\n}";

    /// <summary>
    /// Constructs a lookup with repeated keys and ordered values.
    /// </summary>
    /// <param name="edited">Whether to change the second group value.</param>
    /// <returns>The complete selected method declaration.</returns>
    public static string Method(bool edited) => ".method public static object Read() {\n"
        + "ldc.i4.3\nnewarr int32\ndup\nldc.i4.0\nldc.i4.s 42\nstelem.i4\n"
        + "dup\nldc.i4.1\nldc.i4.s " + (edited ? "45" : "43") + "\nstelem.i4\n"
        + "dup\nldc.i4.2\nldc.i4.s 44\nstelem.i4\nldnull\nldftn string Key(int32)\n"
        + "newobj instance void class Func<int32, string>::.ctor(object, native int)\n"
        + "call class ILookup<string, int32> Enumerable::ToLookup<int32, string>("
        + "class IEnumerable<int32>, class Func<int32, string>)\nret\n}";
}
