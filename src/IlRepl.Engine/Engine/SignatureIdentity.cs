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
    /// The method's own generic parameters as they appear in a signature's types, by position.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <returns>The parameters found, ordered by position.</returns>
    public static IReadOnlyList<Type> MethodParametersOf(MethodSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var found = new Dictionary<int, Type>();
        void Visit(Type type)
        {
            if (type.IsGenericParameter)
            {
                if (type.DeclaringMethod is not null)
                {
                    found.TryAdd(type.GenericParameterPosition, type);
                }

                return;
            }

            if (type.HasElementType)
            {
                Visit(type.GetElementType()!);
            }
            else if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    Visit(argument);
                }
            }
        }

        Visit(signature.ReturnType);
        foreach (var parameter in signature.ParameterTypes)
        {
            Visit(parameter);
        }

        foreach (var parameter in signature.TypeParameters)
        {
            foreach (var constraint in parameter.Constraints)
            {
                Visit(constraint);
            }
        }

        return [.. found.OrderBy(p => p.Key).Select(p => p.Value)];
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
