namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies member metadata whose only reference to a separate session type is inside an exact signature.
/// </summary>
public static class SignatureDependencyExamples
{
    /// <summary>
    /// Builds a callable owner with a modifier in the requested signature position.
    /// </summary>
    /// <param name="shape">The modified member signature.</param>
    /// <param name="required">Whether the modifier is required.</param>
    /// <returns>The marker and owner declarations.</returns>
    public static string Source(string shape, bool required)
    {
        var modified = "int32 " + (required ? "modreq" : "modopt") + "(Marker)";
        var members = shape switch
        {
            "field" => ".field public static " + modified + " Value\n",
            "nested" => ".field public method void *(method " + modified + " *()) Pointer\n",
            "property" => ".method public static specialname int32 get_Value() {\nldc.i4.s 41\nret\n}\n.property "
                + modified + " Value() {\n.get int32 Owner::get_Value()\n}\n",
            "abstract" => ".method public abstract virtual instance " + modified + " Abstract(" + modified + " value) { }\n",
            "constructor" => ".method public specialname rtspecialname instance void .ctor(" + modified + " value) {\n"
                + "ldarg.0\ncall instance void Object::.ctor()\nret\n}\n",
            _ => "",
        };

        var setup = shape == "property" ? "call int32 Owner::get_Value()\npop\n"
            : shape == "constructor" ? "ldarg.0\nnewobj instance void Owner::.ctor(" + modified + ")\npop\n" : "";
        return ".class public Marker {\n.field public int32 Original\n}\n.class public " + (shape == "abstract" ? "abstract " : "")
            + "Owner {\n" + members + ".method public static " + (shape == "return" ? modified : "int32") + " Read("
            + (shape == "parameter" ? modified : "int32") + " value) {\n" + setup + "ldarg.0\nret\n}\n}";
    }
}
