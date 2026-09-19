namespace IlRepl.Tests.Shared;

/// <summary>
/// Exercises real overrides and selected base calls through virtual, constrained, direct, and delegate dispatch.
/// </summary>
public static class VirtualComparisonExamples
{
    /// <summary>
    /// Declares the selected virtual method and its declaring type.
    /// </summary>
    /// <param name="generic">Whether the owner and method both have type parameters.</param>
    /// <param name="interfaceType">Whether the selected body is a default interface method.</param>
    /// <returns>The complete original type.</returns>
    public static string Source(bool generic, bool interfaceType = false) => ".class public "
        + (interfaceType ? "interface abstract " : "") + "Owner" + (generic ? "<T>" : "") + " {\n"
        + (interfaceType ? "" : ".method public instance void .ctor() {\nldarg.0\ncall instance void Object::.ctor()\nret\n}\n")
        + Method(generic, false) + "\n}";

    /// <summary>
    /// Adds the original or edited increment to the supplied value.
    /// </summary>
    /// <param name="generic">Whether to declare a method type parameter.</param>
    /// <param name="edited">Whether to add two instead of one.</param>
    /// <returns>The complete selected method.</returns>
    public static string Method(bool generic, bool edited) => ".method public virtual newslot instance int32 Read"
        + (generic ? "<U>" : "") + "(int32 value) {\nldarg value\n" + (edited ? "ldc.i4.2" : "ldc.i4.1") + "\nadd\nret\n}";

    /// <summary>
    /// Selects a closed owner and method when generic parameters are present.
    /// </summary>
    /// <param name="generic">Whether to supply type arguments.</param>
    /// <returns>The complete method reference.</returns>
    public static string Reference(bool generic) => "instance int32 " + (generic ? "Owner<int32>::Read<string>" : "Owner::Read")
        + "(int32)";

    /// <summary>
    /// Declares a derived receiver and invokes the copied base slot with an identical input in both versions.
    /// </summary>
    /// <param name="kind">The direct, virtual, constrained, or delegate invocation.</param>
    /// <param name="behavior">An override, a base call, an inherited slot, a new slot, or an explicit override.</param>
    /// <param name="generic">Whether the copied owner and selected method are generic.</param>
    /// <returns>The derived type and complete scenario.</returns>
    public static string Scenario(string kind, string behavior, bool generic)
    {
        var interfaceType = behavior.StartsWith("interface-", StringComparison.Ordinal);
        if (interfaceType)
        {
            behavior = behavior[10..];
        }

        var owner = "IlRepl.Edits.Copy.Owner" + (generic ? "`1<int32>" : "");
        var read = "instance int32 " + owner + "::Read" + (generic ? "<string>" : "") + "(int32)";
        var body = behavior == "inherit" ? "" : ".method public virtual " + (behavior == "newslot" ? "newslot " : "")
            + "instance int32 " + (behavior == "explicit" ? "Other" : "Read") + (generic ? "<U>" : "") + "(int32 value) {\n"
            + (behavior == "explicit" ? ".override " + owner + "::Read\n" : "")
            + (behavior == "base" ? "ldarg.0\nldarg value\ncall instance int32 " + owner + "::Read"
                + (generic ? "<!!0>" : "") + "(int32)\n" : "ldarg value\n")
            + (behavior == "same" ? "ldc.i4.1" : "ldc.i4.s 100") + "\nadd\nret\n}\n";
        var invoke = kind == "delegate" ? "dup\nldvirtftn " + read
            + "\nnewobj instance void class Func<int32, int32>::.ctor(object, native int)\nldc.i4.7\n"
            + "callvirt instance !1 class Func<int32, int32>::Invoke(!0)"
            : "ldc.i4.7\n" + (kind == "constrained" ? "constrained. Derived\n" : "")
                + (kind == "call" ? "call " : "callvirt ") + read;
        return ".class public Derived " + (interfaceType ? "implements " : "extends ") + owner
            + " {\n.method public instance void .ctor() {\nldarg.0\ncall instance void "
            + (interfaceType ? "Object" : owner) + "::.ctor()\nret\n}\n" + body + "}\n"
            + ".method int32 Scenario() {\n.locals init (class Derived receiver)\nnewobj instance void Derived::.ctor()\nstloc.0\n"
            + (kind == "constrained" ? "ldloca.s 0\n" : "ldloc.0\n") + invoke + "\nret\n}";
    }
}
