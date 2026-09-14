namespace IlRepl.Tests.Shared;

/// <summary>
/// Binds public members by name without embedding a method token or reflecting over the copied owner first.
/// </summary>
public static class DelegateReflectionExamples
{
    /// <summary>
    /// Declares the owner and its otherwise unreferenced delegate target.
    /// </summary>
    /// <param name="staticTarget">Whether the named target is static.</param>
    /// <param name="options">The number of delegate-binding options supplied.</param>
    /// <returns>The complete type declaration.</returns>
    public static string Source(bool staticTarget, int options) => """
        .class public Owner {
          .method public instance void .ctor() {
            ldarg.0
            call instance void Object::.ctor()
            ret
          }
        """ + "\n.method public " + (staticTarget ? "static" : "instance") + " int32 Hidden() {\nldc.i4.s 42\nret\n}\n"
        + Method(staticTarget, options, false) + "\n}";

    /// <summary>
    /// Selects an ordinary, case-insensitive, or explicitly throwing name-based delegate factory overload.
    /// </summary>
    /// <param name="staticTarget">Whether the named target is static.</param>
    /// <param name="options">The number of delegate-binding options supplied.</param>
    /// <param name="edited">Whether to change the returned value.</param>
    /// <returns>The selected method declaration.</returns>
    public static string Method(bool staticTarget, int options, bool edited) => ".method public "
        + (staticTarget ? "static int32 Read(class Type delegateType, class Type target)" : "instance int32 Read(class Type delegateType)")
        + " {\n" + (staticTarget ? "ldarg.0\nldarg.1" : "ldarg.1\nldarg.0") + "\nldstr \""
        + (options == 0 ? "Hidden" : "hidden") + "\"\n" + string.Concat(Enumerable.Repeat("ldc.i4.1\n", options))
        + "call class Delegate Delegate::CreateDelegate(class Type, " + (staticTarget ? "class Type" : "object") + ", string"
        + string.Concat(Enumerable.Repeat(", bool", options)) + ")\ncastclass class Func<int32>\n"
        + "callvirt instance !0 class Func<int32>::Invoke()\n" + (edited ? "ldc.i4.1\nadd\n" : "") + "ret\n}";

    /// <summary>
    /// Selects the method that performs the name-based lookup.
    /// </summary>
    /// <param name="staticTarget">Whether the named target is static.</param>
    /// <returns>The complete method reference.</returns>
    public static string Reference(bool staticTarget) => staticTarget
        ? "int32 Owner::Read(class Type, class Type)" : "instance int32 Owner::Read(class Type)";

    /// <summary>
    /// Supplies the copied receiver or owner type to the selected alias in both comparison workers.
    /// </summary>
    /// <param name="staticTarget">Whether the named target is static.</param>
    /// <returns>The complete scenario declaration.</returns>
    public static string Scenario(bool staticTarget) => ".method int32 Scenario() {\n" + (staticTarget ? ""
        : "newobj instance void IlRepl.Edits.Copy.Owner::.ctor()\n")
        + "ldtoken class Func<int32>\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n" + (staticTarget
        ? "ldtoken IlRepl.Edits.Copy.Owner\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n" : "")
        + "call Copy\nret\n}";
}
