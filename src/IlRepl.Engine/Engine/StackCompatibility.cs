using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Decides whether a value on the simulated stack can be returned from a method with a declared
/// return type. The rules are the ones the JIT applies to <c>ret</c>: the stack categories must
/// agree, an int32 may fill a native int, references must be assignable, and value types must match.
/// </summary>
public static class StackCompatibility
{
    /// <summary>
    /// Classifies a type by how it lives on the evaluation stack.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The stack category.</returns>
    public static StackCategory Category(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsByRef)
        {
            return StackCategory.ByRef;
        }

        if (type.IsPointer || type == typeof(nint) || type == typeof(nuint))
        {
            return StackCategory.NativeInt;
        }

        if (type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
        }

        if (type == typeof(bool) || type == typeof(char) || type == typeof(sbyte) || type == typeof(byte)
            || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint))
        {
            return StackCategory.Int32;
        }

        if (type == typeof(long) || type == typeof(ulong))
        {
            return StackCategory.Int64;
        }

        if (type == typeof(float) || type == typeof(double))
        {
            return StackCategory.Float;
        }

        if (type.IsGenericParameter)
        {
            return StackCategory.ObjectReference;
        }

        return type.IsValueType ? StackCategory.ValueType : StackCategory.ObjectReference;
    }

    /// <summary>
    /// True when a value of the given stack type can be returned where <paramref name="declared"/> is expected.
    /// </summary>
    /// <param name="actual">The type on the stack; null when the model could not infer it, which is accepted.</param>
    /// <param name="declared">The declared return type.</param>
    /// <returns>Whether <c>ret</c> is valid.</returns>
    public static bool CanReturn(Type? actual, Type declared)
    {
        ArgumentNullException.ThrowIfNull(declared);
        if (actual is null)
        {
            return true;
        }

        var expected = Category(declared);
        if (actual == typeof(NullReferenceMarker))
        {
            return expected == StackCategory.ObjectReference;
        }

        var found = Category(actual);
        return expected switch
        {
            StackCategory.Int32 or StackCategory.Int64 or StackCategory.Float => found == expected,
            StackCategory.NativeInt => found is StackCategory.NativeInt or StackCategory.Int32,
            StackCategory.ByRef => found == StackCategory.ByRef && MemberResolver.TypesEqual(actual.GetElementType()!, declared.GetElementType()!),
            StackCategory.ValueType => found == StackCategory.ValueType && MemberResolver.TypesEqual(actual, declared),
            _ => found == StackCategory.ObjectReference && Assignable(declared, actual),
        };
    }

    private static bool Assignable(Type declared, Type actual)
    {
        // Assignability is not defined for types that are still being built or for open
        // generic parameters; the JIT settles those when the cell runs.
        if (declared.IsGenericParameter || actual.IsGenericParameter || declared is TypeBuilder || actual is TypeBuilder)
        {
            return true;
        }

        return declared.IsAssignableFrom(actual);
    }
}
