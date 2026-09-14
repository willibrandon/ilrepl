namespace IlRepl.Tests.Shared;

/// <summary>
/// Activates copied types through assembly receivers with observable constructor state.
/// </summary>
public static class AssemblyActivationExamples
{
    /// <summary>
    /// Declares owners and constructors that are reachable only through string activation.
    /// </summary>
    /// <param name="overload">The number of activation parameters.</param>
    /// <param name="nested">Whether to activate a nested generic owner.</param>
    /// <param name="dispatch">The call, callvirt, or constrained tail dispatch.</param>
    /// <returns>The complete source declarations.</returns>
    public static string Source(int overload, bool nested, string dispatch) => ".class public Activation.Owner {\n"
        + Constructors("Activation.Owner") + "\n.class nested public Nested<T> {\n"
        + Constructors("class Activation.Owner/Nested<!0>") + "\n}\n"
        + (dispatch == "tail" ? ".method private static object Make<(class Assembly) T>(!!0& scope, string name) {\n"
            + "ldarg.0\nldarg.1\n" + Options(overload) + "constrained. !!0\ntail.\ncallvirt instance object Assembly::CreateInstance("
            + Signature(overload) + ")\nret\n}\n" : "")
        + Method(overload, nested, dispatch, false) + "\n}";

    /// <summary>
    /// Reads state from the activated copy, proving both type translation and constructor execution.
    /// </summary>
    /// <param name="overload">The number of activation parameters.</param>
    /// <param name="nested">Whether to activate a nested generic owner.</param>
    /// <param name="dispatch">The call, callvirt, or constrained tail dispatch.</param>
    /// <param name="edited">Whether to increment the result.</param>
    /// <returns>The complete selected method.</returns>
    public static string Method(int overload, bool nested, string dispatch, bool edited)
    {
        var owner = "Activation.Owner" + (nested ? "/Nested<int32>" : "");
        return ".method public static int32 Read(string name) {\n.locals init (class Assembly scope)\n"
            + "call class Assembly Assembly::GetExecutingAssembly()\n"
            + (dispatch == "tail" ? "stloc.0\nldloca.s 0\nldarg.0\n"
                + "call object Activation.Owner::Make<class Assembly>(!!0&, string)\n"
                : "ldarg.0\n" + Options(overload) + dispatch + " instance object Assembly::CreateInstance(" + Signature(overload) + ")\n")
            + "castclass " + owner + "\nldfld int32 " + owner + "::Value\n"
            + (edited ? "ldc.i4.1\nadd\n" : "") + "ret\n}";
    }

    private static string Signature(int overload) => "string" + (overload == 1 ? "" : ", bool")
        + (overload == 7 ? ", valuetype BindingFlags, class Binder, object[], class CultureInfo, object[]" : "");

    private static string Options(int overload) => overload == 1 ? "" : "ldc.i4.1\n" + (overload == 7
        ? "ldc.i4 548\nldnull\nldc.i4.1\nnewarr object\ndup\nldc.i4.0\nldc.i4.s 41\nbox int32\nstelem.ref\nldnull\nldnull\n" : "");

    private static string Constructors(string owner) => $$"""
        .field public int32 Value
        .method public instance void .ctor() {
          ldarg.0
          call instance void Object::.ctor()
          ldarg.0
          ldc.i4.s 42
          stfld int32 {{owner}}::Value
          ret
        }
        .method private instance void .ctor(int32 value) {
          ldarg.0
          call instance void Object::.ctor()
          ldarg.0
          ldarg.1
          ldc.i4.1
          add
          stfld int32 {{owner}}::Value
          ret
        }
        """;
}
