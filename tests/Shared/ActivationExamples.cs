namespace IlRepl.Tests.Shared;

/// <summary>
/// Activates copied owners and nested generic types using assembly-scoped string names.
/// </summary>
public static class ActivationExamples
{
    /// <summary>
    /// Declares constructors with observable arguments and a method that verifies the activated instance's identity.
    /// </summary>
    /// <param name="overload">The number of activation parameters.</param>
    /// <param name="nested">Whether to instantiate a nested generic type.</param>
    /// <param name="fromFile">Whether to load an assembly file.</param>
    /// <returns>The complete source declarations.</returns>
    public static string Source(int overload, bool nested, bool fromFile = false) => ".class public Activation.Owner {\n"
        + Constructors() + "\n.class nested public Nested<T> {\n" + Constructors() + "\n}\n"
        + Method(overload, nested, false, fromFile) + "\n}";

    /// <summary>
    /// Calls the chosen activation overload and compares its runtime type with the copied type token.
    /// </summary>
    /// <param name="overload">The number of activation parameters.</param>
    /// <param name="nested">Whether to instantiate a nested generic type.</param>
    /// <param name="edited">Whether to increment the observed result.</param>
    /// <param name="fromFile">Whether to load an assembly file.</param>
    /// <returns>The selected method declaration.</returns>
    public static string Method(int overload, bool nested, bool edited, bool fromFile = false)
    {
        const string handle = "class System.Runtime.Remoting.ObjectHandle";
        var options = overload == 8 ? "ldc.i4.1\nldc.i4 532\nldnull\nldc.i4.1\nnewarr object\ndup\nldc.i4.0\n"
            + "ldc.i4.s 42\nbox int32\nstelem.ref\nldnull\nldnull\n" : overload == 3 ? "ldnull\n" : "";
        var signature = overload == 8 ? ", bool, valuetype BindingFlags, class Binder, object[], class CultureInfo, object[]"
            : overload == 3 ? ", object[]" : "";
        return ".method public static int32 Read(string assembly, string name) {\nldarg.0\nldarg.1\n" + options
            + "call " + handle + " Activator::" + (fromFile ? "CreateInstanceFrom" : "CreateInstance")
            + "(string, string" + signature + ")\ncallvirt instance object " + handle + "::Unwrap()\n"
            + "callvirt instance class Type Object::GetType()\nldtoken Activation.Owner" + (nested ? "/Nested<int32>" : "")
            + "\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\nceq\nldc.i4.s 42\nmul\n"
            + "ldarg.0\ncall void Console::WriteLine(string)\nldarg.1\ncall void Console::WriteLine(string)\n"
            + (edited ? "ldc.i4.1\nadd\n" : "") + "ret\n}";
    }

    /// <summary>
    /// Supplies an original type name, including the case variation accepted by the full overload.
    /// </summary>
    /// <param name="nested">Whether to name the nested generic type.</param>
    /// <param name="ignoreCase">Whether to vary the original spelling's case.</param>
    /// <returns>The runtime activation name.</returns>
    public static string Name(bool nested, bool ignoreCase)
    {
        var name = "Activation.Owner" + (nested ? "+Nested`1[[System.Int32, System.Private.CoreLib]]" : "");
        return ignoreCase ? name.ToLowerInvariant() : name;
    }

    private static string Constructors() => """
        .method public instance void .ctor() {
          ldarg.0
          call instance void Object::.ctor()
          ldc.i4.s 42
          call void Console::WriteLine(int32)
          ret
        }
        .method public instance void .ctor(int32 value) {
          ldarg.0
          call instance void Object::.ctor()
          ldarg.1
          call void Console::WriteLine(int32)
          ret
        }
        """;
}
