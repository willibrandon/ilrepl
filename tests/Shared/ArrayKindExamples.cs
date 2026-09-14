namespace IlRepl.Tests.Shared;

/// <summary>
/// Produces real arrays and reflected types that distinguish vectors from rank-one multidimensional arrays.
/// </summary>
public static class ArrayKindExamples
{
    /// <summary>
    /// Declares a method whose revision changes only the observed array kind or its reflected type.
    /// </summary>
    /// <param name="kind">The array value or reflected type shape.</param>
    /// <param name="edited">Whether to use the non-vector array type.</param>
    /// <returns>The complete method declaration.</returns>
    public static string Source(string kind, bool edited)
    {
        var array = edited ? "int32[0...]" : "int32[]";
        if (kind == "array")
        {
            return ".method public static object Read() {\nldc.i4.2\nnewarr " + array + "\nret\n}";
        }

        if (kind == "bounds")
        {
            return ".method public static class Array Read() {\n" + (edited ? """
                ldtoken int32
                call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)
                ldc.i4.1
                newarr int32
                dup
                ldc.i4.0
                ldc.i4.2
                stelem.i4
                ldc.i4.1
                newarr int32
                dup
                ldc.i4.0
                ldc.i4.m1
                stelem.i4
                call class Array Array::CreateInstance(class Type, int32[], int32[])
                """ : "ldc.i4.2\nnewarr int32") + "\nret\n}";
        }

        var target = kind switch
        {
            "generic" => "class List<" + array + ">",
            "jagged" => array + "[]",
            _ => array,
        };
        return ".method public static class Type Read() {\nldtoken " + target
            + "\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\nret\n}";
    }
}
