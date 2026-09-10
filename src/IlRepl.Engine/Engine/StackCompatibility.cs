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

        if (type.IsGenericParameter)
        {
            // A parameter's constraints decide at run time; a builder cannot even be asked whether it is an enum.
            return StackCategory.ObjectReference;
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

        return type.IsValueType ? StackCategory.ValueType : StackCategory.ObjectReference;
    }

    /// <summary>
    /// True when a value of the given stack type can be returned where <paramref name="declared"/> is expected.
    /// A boxed value (<see cref="Boxed{T}"/>) can be returned wherever its value type is assignable,
    /// a reference the model could not type (<see cref="UnknownReferenceMarker"/>) is accepted for
    /// any reference type, and a value that is exactly <c>object</c> is not narrowed, because
    /// returning it as a narrower type would be type confusion.
    /// </summary>
    /// <param name="actual">The type on the stack; null when the model could not infer it, which is accepted.</param>
    /// <param name="declared">The declared return type.</param>
    /// <returns>Whether <c>ret</c> is valid.</returns>
    public static bool CanReturn(Type? actual, Type declared) => CanReturn(actual, declared, TypeTable.Empty);

    /// <summary>
    /// True when a value of the given stack type can be returned where <paramref name="declared"/> is expected,
    /// with session types judged through the declarations in <paramref name="types"/>.
    /// </summary>
    /// <param name="actual">The type on the stack; null when the model could not infer it, which is accepted.</param>
    /// <param name="declared">The declared return type.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <returns>Whether <c>ret</c> is valid.</returns>
    public static bool CanReturn(Type? actual, Type declared, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(types);
        return Binding.StackReturnRules.Accepts(
            actual,
            declared,
            Category,
            type => type == typeof(NullReferenceMarker) || type == typeof(UnknownReferenceMarker),
            StackSimulator.BoxedType,
            (from, to) => Assignable(to, from, types),
            type => type.GetElementType()!,
            (left, right) => TypeIdentity.Equal(left, right));
    }

    private static bool Assignable(Type declared, Type actual, TypeTable types)
    {
        // Open generic parameters are settled by their constraints when the cell runs; a
        // session type answers through its declaration, since a builder cannot be asked.
        if (declared.IsGenericParameter || actual.IsGenericParameter)
        {
            return true;
        }

        return TypeRelations.IsAssignable(actual, declared, types);
    }
}
