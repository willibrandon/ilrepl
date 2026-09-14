namespace IlRepl.Tests.Shared;

/// <summary>
/// Exercises receiver mutation through direct, virtual, constrained, and delegate calls to the selected method.
/// </summary>
public static class ComparisonReceiverCallExamples
{
    /// <summary>
    /// Declares a reference or value type with mutable receiver state.
    /// </summary>
    /// <param name="valueType">Whether the receiver is a struct.</param>
    /// <param name="generic">Whether its owner declares a type parameter.</param>
    /// <returns>The complete source type.</returns>
    public static string Source(bool valueType, bool generic) => ".class public " + (valueType ? "sequential sealed " : "")
        + "Owner" + (generic ? "<T>" : "") + (valueType ? " extends ValueType" : "") + " {\n.field public int32 Value\n"
        + ".method public instance void .ctor(int32 value) {\n" + (valueType ? "" : "ldarg.0\ncall instance void Object::.ctor()\n")
        + "ldarg.0\nldarg.1\nstfld int32 " + Owner(generic, false) + "::Value\nret\n}\n" + Method(generic, false) + "\n}";

    /// <summary>
    /// Mutates the receiver and returns the actual stored value.
    /// </summary>
    /// <param name="generic">Whether the declaring type has a type parameter.</param>
    /// <param name="edited">Whether the increment is two instead of one.</param>
    /// <returns>The complete selected method declaration.</returns>
    public static string Method(bool generic, bool edited) => ".method public instance int32 Read() {\nldarg.0\ndup\nldfld int32 "
        + Owner(generic, false) + "::Value\n" + (edited ? "ldc.i4.2" : "ldc.i4.1") + "\nadd\nstfld int32 "
        + Owner(generic, false) + "::Value\nldarg.0\nldfld int32 " + Owner(generic, false) + "::Value\nret\n}";

    /// <summary>
    /// Selects the receiver method with a closed owner when needed.
    /// </summary>
    /// <param name="generic">Whether to supply an owner type argument.</param>
    /// <returns>The exact source method reference.</returns>
    public static string Reference(bool generic) => "instance int32 " + (generic ? "Owner<int32>" : "Owner") + "::Read()";

    /// <summary>
    /// Calls the copied receiver and verifies its stored mutation, including calls made after a caught null-receiver failure.
    /// </summary>
    /// <param name="kind">The call, virtual, constrained, delegate, or corresponding null-receiver operation.</param>
    /// <param name="valueType">Whether the receiver is a struct.</param>
    /// <param name="generic">Whether the owner is generic.</param>
    /// <returns>The complete parameterless scenario.</returns>
    public static string Scenario(string kind, bool valueType, bool generic)
    {
        var owner = Owner(generic, true);
        var typed = (valueType ? "valuetype " : "class ") + owner;
        var read = "instance int32 " + owner + "::Read()\n";
        var invoke = kind.EndsWith("delegate", StringComparison.Ordinal)
            ? "dup\nldvirtftn " + read + "newobj instance void class Func`1<int32>::.ctor(object, native int)\n"
                + "callvirt instance !0 class Func`1<int32>::Invoke()\n"
            : (kind.EndsWith("virtual", StringComparison.Ordinal) ? "callvirt " : "call ") + read;
        var nullCall = kind.StartsWith("null-", StringComparison.Ordinal)
            ? ".try {\nldnull\n" + invoke + "pop\nleave READY\n} catch NullReferenceException {\npop\nleave READY\n}\nREADY: nop\n"
            : "";
        var load = valueType || kind == "constrained" ? "ldloca.s 0\n" : "ldloc.0\n";
        return ".method int32 Scenario() {\n.locals init (" + typed + " receiver)\n" + nullCall
            + "ldc.i4.7\nnewobj instance void " + owner + "::.ctor(int32)\nstloc.0\n" + load
            + (kind == "constrained" ? "constrained. " + typed + "\ncallvirt " + read
                : kind.StartsWith("null-", StringComparison.Ordinal) ? "call " + read : invoke)
            + "pop\n" + (valueType ? "ldloca.s 0\n" : "ldloc.0\n") + "ldfld int32 " + owner + "::Value\nret\n}";
    }

    private static string Owner(bool generic, bool copied) => (copied ? "IlRepl.Edits.Copy." : "") + "Owner"
        + (generic ? "`1<" + (copied ? "int32" : "!0") + ">" : "");
}
