namespace IlRepl.Tests.Shared;

/// <summary>
/// Exercises null and explicit resolver delegates on string-based type lookups.
/// </summary>
public static class TypeLookupResolverExamples
{
    /// <summary>
    /// Declares resolver callbacks that detect changed input names or unexpected assembly resolution.
    /// </summary>
    /// <param name="resolver">The callback to supply, or none.</param>
    /// <param name="options">The number of Boolean lookup options.</param>
    /// <returns>The owner and selected lookup method.</returns>
    public static string Source(string resolver, int options) => """
        .class public Lookup.Owner {
          .method private static class Assembly ResolveAssembly(class AssemblyName name) {
            ldstr "unexpected assembly resolution"
            newobj instance void InvalidOperationException::.ctor(string)
            throw
          }
          .method private static class Type ResolveType(class Assembly assembly, string name, bool ignoreCase) {
            ldarg.1
            call void Console::WriteLine(string)
            ldtoken Lookup.Owner
            call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)
            ret
          }
        """ + "\n" + Method(resolver, options) + "\n}";

    /// <summary>
    /// Looks up an unqualified owner while honoring explicitly supplied callbacks.
    /// </summary>
    /// <param name="resolver">The callback to supply, or none.</param>
    /// <param name="options">The number of Boolean lookup options.</param>
    /// <returns>The selected method declaration.</returns>
    public static string Method(string resolver, int options) => ".method public static bool Read() {\nldstr \"Lookup.Owner\"\n"
        + (resolver == "assembly" ? "ldnull\nldftn class Assembly Lookup.Owner::ResolveAssembly(class AssemblyName)\n"
            + "newobj instance void class Func<class AssemblyName, class Assembly>::.ctor(object, native int)\n" : "ldnull\n")
        + (resolver == "type" ? "ldnull\nldftn class Type Lookup.Owner::ResolveType(class Assembly, string, bool)\n"
            + "newobj instance void class Func<class Assembly, string, bool, class Type>::.ctor(object, native int)\n" : "ldnull\n")
        + string.Concat(Enumerable.Repeat("ldc.i4.1\n", options))
        + "call class Type Type::GetType(string, class Func<class AssemblyName, class Assembly>, "
        + "class Func<class Assembly, string, bool, class Type>" + string.Concat(Enumerable.Repeat(", bool", options)) + ")\n"
        + "ldtoken Lookup.Owner\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\nceq\nret\n}";
}
