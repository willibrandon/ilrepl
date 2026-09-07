namespace IlRepl.Engine;

/// <summary>
/// Decides when two types are the same for the purposes of matching signatures. Generic
/// parameters are the same only when they belong to the same declaration, are of the same kind
/// (a type's or a method's), and sit at the same position; <c>!0</c> is never <c>!!0</c>, and
/// the <c>T</c> of one type is never the <c>T</c> of another. An <see cref="EmitMap"/> may
/// declare two owners equivalent, which is how a prototype's parameters match the real ones.
/// </summary>
public static class TypeIdentity
{
    /// <summary>
    /// True when the two types are the same.
    /// </summary>
    /// <param name="a">The first type.</param>
    /// <param name="b">The second type.</param>
    /// <param name="map">A map whose entries make two owners equivalent, or null.</param>
    /// <returns>True when they match.</returns>
    public static bool Equal(Type a, Type b, EmitMap? map = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (ReferenceEquals(a, b) || (a is System.Reflection.Emit.TypeBuilder == false && b is System.Reflection.Emit.TypeBuilder == false && a == b))
        {
            return true;
        }

        if (a.IsGenericParameter || b.IsGenericParameter)
        {
            return a.IsGenericParameter && b.IsGenericParameter && SameParameter(a, b, map);
        }

        if (a.IsByRef && b.IsByRef)
        {
            return Equal(a.GetElementType()!, b.GetElementType()!, map);
        }

        if (a.IsPointer && b.IsPointer)
        {
            return Equal(a.GetElementType()!, b.GetElementType()!, map);
        }

        if (a.IsArray && b.IsArray)
        {
            // int32[] is a vector and int32[0...] is a rank-1 array; they are different types.
            return a.IsSZArray == b.IsSZArray && a.GetArrayRank() == b.GetArrayRank() && Equal(a.GetElementType()!, b.GetElementType()!, map);
        }

        if (a.IsGenericType && b.IsGenericType && !a.IsGenericTypeDefinition && !b.IsGenericTypeDefinition)
        {
            if (!Equal(a.GetGenericTypeDefinition(), b.GetGenericTypeDefinition(), map))
            {
                return false;
            }

            var aa = a.GetGenericArguments();
            var ba = b.GetGenericArguments();
            return aa.Length == ba.Length && aa.Zip(ba).All(p => Equal(p.First, p.Second, map));
        }

        return map is not null && (ReferenceEquals(map.Map(a), b) || ReferenceEquals(map.Map(b), a));
    }

    private static bool SameParameter(Type a, Type b, EmitMap? map)
    {
        var aMethod = a.DeclaringMethod is not null;
        var bMethod = b.DeclaringMethod is not null;
        if (aMethod != bMethod || a.GenericParameterPosition != b.GenericParameterPosition)
        {
            return false;
        }

        if (aMethod)
        {
            return SameOwner(a.DeclaringMethod, b.DeclaringMethod, map);
        }

        return SameOwner(a.DeclaringType, b.DeclaringType, map);
    }

    private static bool SameOwner(System.Reflection.MethodBase? a, System.Reflection.MethodBase? b, EmitMap? map)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (ReferenceEquals(a, b) || a == b)
        {
            return true;
        }

        return map is not null && (ReferenceEquals(map.Map(a), b) || ReferenceEquals(map.Map(b), a));
    }

    private static bool SameOwner(Type? a, Type? b, EmitMap? map)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (ReferenceEquals(a, b) || (a is not System.Reflection.Emit.TypeBuilder && b is not System.Reflection.Emit.TypeBuilder && a == b))
        {
            return true;
        }

        return map is not null && (ReferenceEquals(map.Map(a), b) || ReferenceEquals(map.Map(b), a));
    }
}
