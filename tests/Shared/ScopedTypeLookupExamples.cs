namespace IlRepl.Tests.Shared;

/// <summary>
/// Resolves original type names within the assembly or module of a copied owner.
/// </summary>
public static class ScopedTypeLookupExamples
{
    /// <summary>
    /// Declares a generic helper that performs a constrained tail call.
    /// </summary>
    /// <param name="scope">The Assembly or Module receiver type.</param>
    /// <returns>The complete source declaration.</returns>
    public static string TailSource(string scope) => ".class public Lookup.Owner {\n"
        + ".method private static class Type Find<(class " + scope + ") T>(!!0& scope, string name) {\nldarg.0\nldarg.1\n"
        + "constrained. !!0\ntail.\n"
        + "callvirt instance class Type " + scope + "::GetType(string)\nret\n}\n"
        + TailMethod(scope) + "\n}";

    /// <summary>
    /// Looks up the owner through a generic helper using its assembly or module by reference.
    /// </summary>
    /// <param name="scope">The Assembly or Module receiver type.</param>
    /// <returns>The selected method declaration.</returns>
    public static string TailMethod(string scope) => ".method public static class Type Read() {\n.locals init (class " + scope + " scope)\n"
        + "ldtoken Lookup.Owner\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\ncallvirt instance class "
        + scope + " " + (scope == "Module" ? "MemberInfo::get_Module" : "Type::get_Assembly") + "()\nstloc.0\nldloca.s 0\n"
        + "ldstr \"Lookup.Owner\"\ncall class Type Lookup.Owner::Find<class " + scope + ">(!!0&, string)\nret\n}";

    /// <summary>
    /// Declares the owner, its nested types, and the selected lookup.
    /// </summary>
    /// <param name="scope">The assembly, executing assembly, or module receiver.</param>
    /// <param name="shape">The type-name shape to resolve.</param>
    /// <param name="options">The number of Boolean lookup options.</param>
    /// <param name="constrained">Whether the call uses a managed reference to the receiver.</param>
    /// <returns>The complete declaration.</returns>
    public static string Source(string scope, int shape, int options, bool constrained) => """
        .class public Lookup.Owner {
          .class nested public Nested {
          }
          .class nested public Generic`1<T> {
          }
        """ + "\n" + Method(scope, shape, options, constrained, false) + "\n}";

    /// <summary>
    /// Compares an instance lookup with the corresponding type token.
    /// </summary>
    /// <param name="scope">The assembly, executing assembly, or module receiver.</param>
    /// <param name="shape">The type-name shape to resolve.</param>
    /// <param name="options">The number of Boolean lookup options.</param>
    /// <param name="constrained">Whether the call uses a managed reference to the receiver.</param>
    /// <param name="edited">Whether to invert the result.</param>
    /// <returns>The selected method declaration.</returns>
    public static string Method(string scope, int shape, int options, bool constrained, bool edited)
    {
        var receiver = scope == "module" ? "Module" : "Assembly";
        var name = shape == 4 ? "Lookup.Owner+Generic`1[Lookup.Owner+Nested[]]" : TypeLookupExamples.Name(shape, false);
        var token = shape switch
        {
            1 => "Lookup.Owner/Nested",
            2 => "class Lookup.Owner[]",
            4 => "class Lookup.Owner/Generic<class Lookup.Owner/Nested[]>",
            _ => "Lookup.Owner",
        };

        return ".method public static bool Read() {\n.locals init (class " + receiver + " scope)\n"
            + (scope == "executing" ? "call class Assembly Assembly::GetExecutingAssembly()\n"
                : "ldtoken Lookup.Owner\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n"
                    + "callvirt instance class " + receiver + " " + (scope == "module" ? "MemberInfo::get_Module" : "Type::get_Assembly")
                    + "()\n")
            + (constrained ? "stloc.0\nldloca.s 0\n" : "")
            + "ldstr \"" + (options == 2 ? name.ToLowerInvariant() : name) + "\"\n"
            + string.Concat(Enumerable.Repeat("ldc.i4.1\n", options))
            + (constrained ? "constrained. class " + receiver + "\n" : "")
            + "callvirt instance class Type " + receiver + "::GetType(string"
            + string.Concat(Enumerable.Repeat(", bool", options)) + ")\n"
            + "ldtoken " + token + "\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n"
            + (shape == 3 ? "ldc.i4.1\ncallvirt instance class Type Type::MakeArrayType(int32)\n" : "")
            + "ceq\n" + (edited ? "ldc.i4.0\nceq\n" : "") + "ret\n}";
    }
}
