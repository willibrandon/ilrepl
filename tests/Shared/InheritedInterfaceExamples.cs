namespace IlRepl.Tests.Shared;

/// <summary>
/// Exercises interface inheritance, reimplementation, and overrides on copied derived types.
/// </summary>
public static class InheritedInterfaceExamples
{
    /// <summary>
    /// Declares the contract, base implementation, and a derived type with a competing method slot.
    /// </summary>
    /// <param name="behavior">Whether the derived type inherits, reimplements, overrides, or explicitly implements the contract.</param>
    /// <param name="generic">Whether the types carry a generic parameter.</param>
    /// <returns>The complete source declarations.</returns>
    public static string Source(string behavior, bool generic)
    {
        var declaration = generic ? "<T>" : "";
        var arguments = generic ? "<!0>" : "";
        var contract = "I" + arguments;
        var inherited = behavior == "interface-inherit";
        var root = inherited ? "IRoot" + arguments : contract;
        var explicitImplementation = behavior is "explicit" or "class-explicit";
        var implementation = behavior == "reimplement" || explicitImplementation ? " implements " + contract : "";
        var overloaded = behavior == "overload";
        var parameter = overloaded ? "int32 value" : "";
        return ".class public interface abstract " + (inherited ? "IRoot" : "I") + declaration + " {\n"
            + (overloaded ? ".method public virtual newslot abstract instance int32 M(!0 value) {\n}\n" : "")
            + ".method public virtual newslot abstract instance int32 M(" + parameter + ") {\n}\n}\n"
            + (inherited ? ".class public interface abstract I" + declaration + " implements " + root + " {\n}\n" : "")
            + ".class public Base" + declaration + " implements " + contract + " {\n"
            + ".method public instance void .ctor() {\nldarg.0\ncall instance void Object::.ctor()\nret\n}\n"
            + (overloaded ? ".method public virtual newslot instance int32 M(!0 value) {\nldc.i4.1\nret\n}\n" : "")
            + ".method public virtual newslot instance int32 M(" + parameter + ") {\n"
            + (overloaded ? "ldc.i4.2" : "ldc.i4.1") + "\nret\n}\n}\n"
            + ".class public Owner" + declaration + " extends Base" + arguments + implementation + " {\n"
            + ".method public instance void .ctor() {\nldarg.0\ncall instance void Base" + arguments + "::.ctor()\nret\n}\n"
            + ".method " + (explicitImplementation ? "private" : "public") + " virtual "
            + (behavior == "override" ? "" : "newslot ") + "instance int32 " + (explicitImplementation ? "Other" : "M")
            + "(" + parameter + ") {\n"
            + (behavior == "explicit" ? ".override " + root + "::M\n" : "") + "ldc.i4.2\nret\n}\n"
            + (behavior == "class-explicit" ? ".override method instance int32 " + root
                + "::M() with method instance int32 Owner" + arguments + "::Other()\n" : "")
            + Method(behavior, generic, false) + "\n}";
    }

    /// <summary>
    /// Calls the interface slot on a newly constructed derived receiver.
    /// </summary>
    /// <param name="behavior">Whether the contract has an inherited interface.</param>
    /// <param name="generic">Whether the owner carries a generic parameter.</param>
    /// <param name="edited">Whether to add ten to the observed implementation's result.</param>
    /// <returns>The selected method declaration.</returns>
    public static string Method(string behavior, bool generic, bool edited)
    {
        var arguments = generic ? "<!0>" : "";
        return ".method public static int32 Read() {\nnewobj instance void Owner" + arguments + "::.ctor()\n"
            + (behavior == "overload" ? "ldc.i4.s 42\n" : "")
            + "callvirt instance int32 " + (behavior == "interface-inherit" ? "IRoot" : "I") + arguments
            + "::M(" + (behavior == "overload" ? "int32" : "") + ")\n"
            + (edited ? "ldc.i4.s 10\nadd\n" : "") + "ret\n}";
    }

    /// <summary>
    /// Selects the method on the original owner, supplying a concrete type argument when needed.
    /// </summary>
    /// <param name="generic">Whether to close the owner with int32.</param>
    /// <returns>The method reference.</returns>
    public static string Reference(bool generic) => "int32 Owner" + (generic ? "<int32>" : "") + "::Read()";
}
