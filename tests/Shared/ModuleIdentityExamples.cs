namespace IlRepl.Tests.Shared;

/// <summary>
/// Observes a copied declaring module's identity through ordinary reflection.
/// </summary>
public static class ModuleIdentityExamples
{
    /// <summary>
    /// Declares a method on a closed or generic owner that returns its module identifier.
    /// </summary>
    /// <param name="generic">Whether the owner has a type parameter.</param>
    /// <returns>The complete session source.</returns>
    public static string Source(bool generic) => ".class public Owner" + (generic ? "<T>" : "") + " {\n"
        + Method(generic, false) + "\n}";

    /// <summary>
    /// Returns the declaring module's MVID, or an intentionally different empty identifier.
    /// </summary>
    /// <param name="generic">Whether the owner has a type parameter.</param>
    /// <param name="changed">Whether to return Guid.Empty.</param>
    /// <returns>The complete selected method.</returns>
    public static string Method(bool generic, bool changed) => ".method public static valuetype Guid Read() {\n"
        + (changed ? "ldsfld valuetype Guid Guid::Empty\n" : "ldtoken " + (generic ? "class Owner`1<!0>" : "Owner")
            + "\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n"
            + "callvirt instance class Module MemberInfo::get_Module()\n"
            + "callvirt instance valuetype Guid Module::get_ModuleVersionId()\n") + "ret\n}";

    /// <summary>
    /// Selects the closed original method for direct invocation.
    /// </summary>
    /// <param name="generic">Whether to supply the owner's type argument.</param>
    /// <returns>The exact method reference.</returns>
    public static string Reference(bool generic) => "valuetype Guid " + (generic ? "Owner<int32>" : "Owner") + "::Read()";
}
