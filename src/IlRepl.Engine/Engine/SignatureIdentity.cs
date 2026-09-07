namespace IlRepl.Engine;

/// <summary>
/// Compares the types of two method signatures the way the CLI matches an implementation to a
/// slot or an overload to another: a method's own generic parameters match by position, since
/// <c>!!0</c> of one method is <c>!!0</c> of the other, while everything else is identity.
/// </summary>
public static class SignatureIdentity
{
    /// <summary>
    /// True when the two types are the same under positional matching of method parameters.
    /// </summary>
    /// <param name="a">The first type.</param>
    /// <param name="b">The second type.</param>
    /// <returns>True when they match.</returns>
    public static bool Equal(Type a, Type b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.IsGenericParameter || b.IsGenericParameter)
        {
            if (a.IsGenericParameter && b.IsGenericParameter && a.DeclaringMethod is not null && b.DeclaringMethod is not null)
            {
                return a.GenericParameterPosition == b.GenericParameterPosition;
            }

            return TypeIdentity.Equal(a, b);
        }

        if (a.HasElementType || b.HasElementType)
        {
            if (a.IsByRef != b.IsByRef || a.IsPointer != b.IsPointer || a.IsArray != b.IsArray)
            {
                return false;
            }

            if (a.IsArray && (a.IsSZArray != b.IsSZArray || a.GetArrayRank() != b.GetArrayRank()))
            {
                return false;
            }

            return Equal(a.GetElementType()!, b.GetElementType()!);
        }

        if (a.IsGenericType && b.IsGenericType && !a.IsGenericTypeDefinition && !b.IsGenericTypeDefinition)
        {
            return TypeIdentity.Equal(a.GetGenericTypeDefinition(), b.GetGenericTypeDefinition())
                && a.GetGenericArguments().Zip(b.GetGenericArguments()).All(p => Equal(p.First, p.Second));
        }

        return TypeIdentity.Equal(a, b);
    }

    /// <summary>
    /// True when two signatures have the same shape: arity, return type, and parameter types.
    /// </summary>
    /// <param name="a">The first signature.</param>
    /// <param name="b">The second signature.</param>
    /// <returns>True when they match.</returns>
    public static bool Same(MethodSignature a, MethodSignature b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.TypeParameters.Count == b.TypeParameters.Count
            && Equal(a.ReturnType, b.ReturnType)
            && a.Parameters.Count == b.Parameters.Count
            && a.ParameterTypes.Zip(b.ParameterTypes).All(p => Equal(p.First, p.Second));
    }
}
