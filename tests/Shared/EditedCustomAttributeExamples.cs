namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies edited attributes with real constructors, named setters and separate session type dependencies.
/// </summary>
public static class EditedCustomAttributeExamples
{
    /// <summary>
    /// Declares an unrelated attribute family and a method with pinned attributes on all three supported targets.
    /// </summary>
    /// <param name="isPrivate">Whether calls require the generated public forwarder.</param>
    /// <returns>The complete original declarations.</returns>
    public static string Source(bool isPrivate) => AttributeDependencyExamples.Source() + "\n.class public Owner {\n"
        + Method(isPrivate, edited: false) + "\n}";

    /// <summary>
    /// Adds distinct and repeated attributes while preserving the original signature and executable value.
    /// </summary>
    /// <param name="isPrivate">Whether the selected method is private.</param>
    /// <param name="edited">Whether to add current attributes instead of declaring the original metadata.</param>
    /// <param name="additionalParameter">Whether to add a parameter with no original metadata.</param>
    /// <returns>The complete method source.</returns>
    public static string Method(bool isPrivate, bool edited, bool additionalParameter = false)
    {
        var result = ".method " + (isPrivate ? "private" : "public") + " static int32 Read(int32 value"
            + (additionalParameter ? ", int32 extra" : "") + ") {\n";
        if (!edited)
        {
            return result + Original("method") + ".param [0]\n" + Original("return") + ".param [1]\n" + Original("parameter")
                + "ldarg.0\nret\n}";
        }

        result += Attribute("method") + Attribute("method") + ".param [0]\n" + Attribute("return")
            + ".param [1]\n" + Attribute("parameter-1") + Attribute("parameter-2");
        if (additionalParameter)
        {
            result += ".param [2]\n" + Attribute("added");
        }

        return result + "nop\n" + Attribute("after-instruction") + "ldarg.0\nret\n}";
    }

    /// <summary>
    /// Observes method, return and parameter attribute counts after invoking the selected executable.
    /// </summary>
    /// <returns>The real reflection scenario, returning 111 originally and 423 after the edit.</returns>
    public static string Scenario() => """
        .method class MethodInfo Selected() {
          ldtoken method int32 Copy(int32)
          call class MethodBase MethodBase::GetMethodFromHandle(valuetype RuntimeMethodHandle)
          castclass MethodInfo
          ret
        }
        .method int32 Scenario() {
          ldc.i4.s 42
          call Copy
          pop
          call Selected
          ldc.i4.0
          callvirt instance object[] MemberInfo::GetCustomAttributes(bool)
          ldlen
          conv.i4
          ldc.i4.s 100
          mul
          call Selected
          callvirt instance class ParameterInfo MethodInfo::get_ReturnParameter()
          ldc.i4.0
          callvirt instance object[] ParameterInfo::GetCustomAttributes(bool)
          ldlen
          conv.i4
          ldc.i4.s 10
          mul
          add
          call Selected
          callvirt instance class ParameterInfo[] MethodBase::GetParameters()
          ldc.i4.0
          ldelem.ref
          ldc.i4.0
          callvirt instance object[] ParameterInfo::GetCustomAttributes(bool)
          ldlen
          conv.i4
          add
          ret
        }
        """;

    private static string Attribute(string name) => ".custom instance void Tag::.ctor(class Type, object, class Type[]) = { "
        + "type(Marker) type(Marker[]) { type(Marker) type(Marker[]) } "
        + "field class Type Kind = type(Marker) property string Name = string('" + name + "') }\n";

    private static string Original(string name) => ".custom instance void ObsoleteAttribute::.ctor(string) = { string('original-"
        + name + "') }\n";
}
