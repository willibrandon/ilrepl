namespace IlRepl.Tests.Shared;

/// <summary>
/// Looks up the original spelling of a type after its declaring context has been renamed.
/// </summary>
public static class TypeLookupExamples
{
    /// <summary>
    /// Declares a named owner and a lookup that also prints its untouched input string.
    /// </summary>
    /// <param name="shape">The type-name shape to resolve.</param>
    /// <param name="options">The number of Boolean lookup options.</param>
    /// <param name="literal">Whether the lookup uses a literal instead of an argument.</param>
    /// <returns>The complete source declaration.</returns>
    public static string Source(int shape, int options, bool literal) => """
        .class public Lookup.Owner {
          .class nested public Nested {
          }
        """ + "\n" + Method(shape, options, literal, false) + "\n}";

    /// <summary>
    /// Compares a string lookup with the corresponding type token.
    /// </summary>
    /// <param name="shape">The type-name shape to resolve.</param>
    /// <param name="options">The number of Boolean lookup options.</param>
    /// <param name="literal">Whether the lookup uses a literal instead of an argument.</param>
    /// <param name="edited">Whether to invert the result.</param>
    /// <returns>The method declaration.</returns>
    public static string Method(int shape, int options, bool literal, bool edited) =>
        ".method public static bool Read(" + (literal ? "" : "string name") + ") {\n"
        + (literal ? "ldstr \"" + Name(shape, options == 2) + "\"" : "ldarg.0")
        + "\ndup\ncall void Console::WriteLine(string)\n" + string.Concat(Enumerable.Repeat("ldc.i4.1\n", options))
        + "call class Type Type::GetType(string" + string.Concat(Enumerable.Repeat(", bool", options)) + ")\n"
        + "ldtoken " + Token(shape) + "\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n"
        + (shape == 3 ? "ldc.i4.1\ncallvirt instance class Type Type::MakeArrayType(int32)\n" : "")
        + "ceq\n" + (edited ? "ldc.i4.0\nceq\n" : "") + "ret\n}";

    /// <summary>
    /// Supplies a reflection name containing copied types at different positions.
    /// </summary>
    /// <param name="shape">The type-name shape to resolve.</param>
    /// <param name="ignoreCase">Whether to use a different spelling case.</param>
    /// <returns>The reflection name.</returns>
    public static string Name(int shape, bool ignoreCase)
    {
        var name = shape switch
        {
            1 => "Lookup.Owner+Nested",
            2 => "Lookup.Owner[]",
            3 => "Lookup.Owner[*]",
            4 => "System.Collections.Generic.List`1[Lookup.Owner+Nested[]]",
            _ => "Lookup.Owner",
        };
        return ignoreCase ? name.ToLowerInvariant() : name;
    }

    private static string Token(int shape) => shape switch
    {
        1 => "Lookup.Owner/Nested",
        2 => "class Lookup.Owner[]",
        3 => "Lookup.Owner",
        4 => "class List<class Lookup.Owner/Nested[]>",
        _ => "Lookup.Owner",
    };
}
