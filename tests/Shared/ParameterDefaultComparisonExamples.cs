using System.Globalization;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds real parameter defaults and scenarios that observe metadata and Missing argument substitution.
/// </summary>
public static class ParameterDefaultComparisonExamples
{
    /// <summary>
    /// Declares an owner whose selected method returns the parameter supplied by its caller.
    /// </summary>
    /// <param name="value">The original default, or null to omit the constant.</param>
    /// <param name="isPrivate">Whether the selected method needs a public callable alias.</param>
    /// <param name="optional">Whether the parameter carries the Optional flag.</param>
    /// <returns>The complete owner declaration.</returns>
    public static string Source(int? value, bool isPrivate, bool optional) =>
        ".class public Owner {\n" + Method(value, isPrivate, optional) + "\n}";

    /// <summary>
    /// Declares an edited default independently of the method parameter's Optional flag.
    /// </summary>
    /// <param name="value">The explicit constant, or null to omit any .param directive.</param>
    /// <param name="isPrivate">Whether the method remains private.</param>
    /// <param name="optional">Whether the parameter remains optional.</param>
    /// <param name="duplicate">Whether an earlier distinct .param constant precedes the final value.</param>
    /// <returns>The complete method declaration.</returns>
    public static string Method(int? value, bool isPrivate, bool optional, bool duplicate = false) =>
        ".method " + (isPrivate ? "private" : "public") + " static int32 Read(" + (optional ? "[opt] " : "")
        + "int32 value) {\n" + (duplicate ? ".param [1] = int32(6)\n" : "")
        + (value is { } number ? ".param [1] = int32(" + number.ToString(CultureInfo.InvariantCulture) + ")\n" : "")
        + "ldarg.0\nret\n}";

    /// <summary>
    /// Creates reflection helpers and scenarios over the selected method's callable alias.
    /// </summary>
    /// <returns>The helper and two complete parameterless scenarios.</returns>
    public static string Scenarios() => """
        .method class ParameterInfo Parameter() {
          ldtoken method int32 Copy(int32)
          call class MethodBase MethodBase::GetMethodFromHandle(valuetype RuntimeMethodHandle)
          callvirt instance class ParameterInfo[] MethodBase::GetParameters()
          ldc.i4.0
          ldelem.ref
          ret
        }
        .method int32 Scenario() {
          ldc.i4.s 42
          call Copy
          pop
          call Parameter
          callvirt instance bool ParameterInfo::get_HasDefaultValue()
          brfalse.s ABSENT
          call Parameter
          callvirt instance object ParameterInfo::get_RawDefaultValue()
          unbox.any int32
          ret
        ABSENT:
          ldc.i4.m1
          ret
        }
        .method int32 MissingScenario() {
          call Parameter
          callvirt instance bool ParameterInfo::get_HasDefaultValue()
          brfalse.s EXPLICIT
          ldtoken method int32 Copy(int32)
          call class MethodBase MethodBase::GetMethodFromHandle(valuetype RuntimeMethodHandle)
          ldnull
          ldc.i4.1
          newarr object
          dup
          ldc.i4.0
          ldsfld object Type::Missing
          stelem.ref
          callvirt instance object MethodBase::Invoke(object, object[])
          unbox.any int32
          ret
        EXPLICIT:
          ldc.i4.7
          call Copy
          ret
        }
        """;
}
