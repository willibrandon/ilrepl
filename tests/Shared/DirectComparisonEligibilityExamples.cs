namespace IlRepl.Tests.Shared;

/// <summary>
/// Provides real methods whose receiver requirements and generic arguments can change independently during editing.
/// </summary>
public static class DirectComparisonEligibilityExamples
{
    /// <summary>
    /// Declares an owner with observable invocation state and an ordinary instance constructor.
    /// </summary>
    /// <param name="isStatic">Whether the original method is static.</param>
    /// <param name="generic">Whether the original method declares a type parameter.</param>
    /// <returns>The complete owner declaration.</returns>
    public static string Source(bool isStatic, bool generic) => ".class public Owner {\n.field public static int32 Runs\n"
        + ".method public instance void .ctor() {\nldarg.0\ncall instance void Object::.ctor()\nret\n}\n"
        + Method(isStatic, generic, false) + "\n}";

    /// <summary>
    /// Declares a method that records execution before returning its original or edited value.
    /// </summary>
    /// <param name="isStatic">Whether the method is static.</param>
    /// <param name="generic">Whether the method declares a type parameter.</param>
    /// <param name="edited">Whether the returned value is 42 instead of 41.</param>
    /// <returns>The complete selected method declaration.</returns>
    public static string Method(bool isStatic, bool generic, bool edited) => ".method public "
        + (isStatic ? "static" : "instance") + " int32 Read" + (generic ? "<T>" : "") + "() {\n"
        + "ldsfld int32 Owner::Runs\nldc.i4.1\nadd\nstsfld int32 Owner::Runs\nldc.i4.s " + (edited ? "42" : "41") + "\nret\n}";

    /// <summary>
    /// Selects the original method with either open or closed generic arguments.
    /// </summary>
    /// <param name="isStatic">Whether the original method is static.</param>
    /// <param name="generic">Whether the original method is generic.</param>
    /// <param name="closed">Whether a generic method is closed over int32.</param>
    /// <returns>The original member reference.</returns>
    public static string Reference(bool isStatic, bool generic, bool closed = true) => (isStatic ? "" : "instance ")
        + "int32 Owner::Read" + (generic ? closed ? "<int32>" : "<[1]>" : "") + "()";

    /// <summary>
    /// Constructs a real receiver when necessary and calls the selected copy through a parameterless scenario.
    /// </summary>
    /// <param name="isStatic">Whether the selected method is static.</param>
    /// <param name="openGeneric">Whether the scenario supplies the method's generic argument.</param>
    /// <returns>The complete scenario declaration.</returns>
    public static string Scenario(bool isStatic, bool openGeneric = false) => ".method int32 Scenario() {\n"
        + (isStatic ? "" : "newobj instance void IlRepl.Edits.Copy.Owner::.ctor()\n")
        + "call Copy" + (openGeneric ? "<int32>" : "") + "\nret\n}";
}
