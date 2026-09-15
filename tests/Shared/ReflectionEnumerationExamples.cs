namespace IlRepl.Tests.Shared;

/// <summary>
/// Enumerates the exact declared methods on public, private, instance, and generic edited owners.
/// </summary>
public static class ReflectionEnumerationExamples
{
    /// <summary>
    /// Declares an owner with exactly two methods and a parameterless constructor.
    /// </summary>
    /// <param name="instance">Whether the selected method has a receiver.</param>
    /// <param name="privateMethod">Whether the selected method needs an accessible session alias.</param>
    /// <param name="generic">Whether both the owner and method declare a generic parameter.</param>
    /// <returns>The complete type declaration.</returns>
    public static string Source(bool instance, bool privateMethod, bool generic) => ".class public Owner" + (generic ? "<T>" : "")
        + " {\n.method public instance void .ctor() {\nldarg.0\ncall instance void Object::.ctor()\nret\n}\n"
        + ".method private static int32 Hidden() {\nldc.i4.s 42\nret\n}\n" + Method(instance, privateMethod, generic, false) + "\n}";

    /// <summary>
    /// Enumerates public and private methods while preserving the selected declaration's signature.
    /// </summary>
    /// <param name="instance">Whether the selected method has a receiver.</param>
    /// <param name="privateMethod">Whether the selected declaration is private.</param>
    /// <param name="generic">Whether the owner and method are generic.</param>
    /// <param name="edited">Whether to add one to the observed method count.</param>
    /// <returns>The complete selected method declaration.</returns>
    public static string Method(bool instance, bool privateMethod, bool generic, bool edited) => ".method "
        + (privateMethod ? "private " : "public ") + (instance ? "instance " : "static ") + "int32 Read" + (generic ? "<U>" : "")
        + "() {\nldtoken " + (generic ? "class Owner`1<!0>" : "Owner")
        + "\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\nldc.i4.s 62\n"
        + "callvirt instance class MethodInfo[] Type::GetMethods(valuetype BindingFlags)\nldlen\nconv.i4\n"
        + (edited ? "ldc.i4.1\nadd\n" : "") + "ret\n}";

    /// <summary>
    /// Selects the original member with closed generic arguments when needed.
    /// </summary>
    /// <param name="instance">Whether the selected method has a receiver.</param>
    /// <param name="generic">Whether the owner and method need type arguments.</param>
    /// <returns>The exact method reference.</returns>
    public static string Reference(bool instance, bool generic) => (instance ? "instance " : "") + "int32 "
        + (generic ? "Owner<int32>::Read<string>()" : "Owner::Read()");

    /// <summary>
    /// Calls the alias through an independently initialized receiver or its selected static signature.
    /// </summary>
    /// <param name="instance">Whether to create a receiver.</param>
    /// <param name="generic">Whether the receiver is a closed generic type.</param>
    /// <returns>The complete scenario declaration.</returns>
    public static string Scenario(bool instance, bool generic) => ".method int32 Scenario() {\n"
        + (instance ? "newobj instance void " + (generic ? "class IlRepl.Edits.Copy.Owner`1<int32>" : "IlRepl.Edits.Copy.Owner")
            + "::.ctor()\n" : "") + "call Copy\nret\n}";
}
