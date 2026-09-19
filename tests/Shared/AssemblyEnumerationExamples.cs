namespace IlRepl.Tests.Shared;

/// <summary>
/// Finds an otherwise unreferenced type through assembly and module enumeration.
/// </summary>
public static class AssemblyEnumerationExamples
{
    /// <summary>
    /// Defines a public helper that only assembly-wide reflection can discover.
    /// </summary>
    /// <param name="api">The enumeration API.</param>
    /// <returns>The complete session declarations.</returns>
    public static string Source(string api) => ".class public Owner {\n.class nested public Hidden { }\n" + Method(api) + "\n}";

    /// <summary>
    /// Returns 42 when enumeration finds Hidden, or zero if the assembly omits it.
    /// </summary>
    /// <param name="api">The enumeration API.</param>
    /// <returns>The complete selected method declaration.</returns>
    public static string Method(string api)
    {
        var query = "call class Assembly Assembly::GetExecutingAssembly()\n" + (api switch
        {
            "GetTypes" or "GetExportedTypes" => "callvirt instance class Type[] Assembly::" + api + "()\n",
            "DefinedTypes" => "callvirt instance class IEnumerable<TypeInfo> Assembly::get_DefinedTypes()\n"
                + "call !!0[] Enumerable::ToArray<class TypeInfo>(class IEnumerable<!!0>)\n",
            "ExportedTypes" => "callvirt instance class IEnumerable<Type> Assembly::get_ExportedTypes()\n"
                + "call !!0[] Enumerable::ToArray<class Type>(class IEnumerable<!!0>)\n",
            "Module.GetTypes" => "callvirt instance class Module Assembly::get_ManifestModule()\n"
                + "callvirt instance class Type[] Module::GetTypes()\n",
            "Module.FindTypes" => "callvirt instance class Module Assembly::get_ManifestModule()\n"
                + "ldsfld class TypeFilter Module::FilterTypeName\nldstr \"Hidden\"\n"
                + "callvirt instance class Type[] Module::FindTypes(class TypeFilter, object)\n",
            _ => throw new ArgumentException("Unknown enumeration API.", nameof(api)),
        });

        return ".method public static int32 Read() {\n.locals init (class Type[] types, int32 index)\n" + query + """
            stloc.0
            ldc.i4.0
            stloc.1
            br check
          next:
            ldloc.0
            ldloc.1
            ldelem.ref
            callvirt instance string Type::get_Name()
            ldstr "Hidden"
            call bool string::op_Equality(string, string)
            brfalse advance
            ldc.i4.s 42
            ret
          advance:
            ldloc.1
            ldc.i4.1
            add
            stloc.1
          check:
            ldloc.1
            ldloc.0
            ldlen
            conv.i4
            blt next
            ldc.i4.0
            ret
          }
          """;
    }
}
