namespace IlRepl.Engine.Binding;

/// <summary>
/// Compares symbol identities across definitions, constructions, parameters, and member instantiations.
/// </summary>
/// <remarks>
/// Decides when two symbols name the same thing, for every source alike. Definitions compare by
/// <see cref="DefinitionId"/>, so two loads of the same bytes are different; constructions compare
/// their definition and every argument; generic parameters compare owner, kind, and position, so
/// <c>!0</c> is never <c>!!0</c> and the <c>T</c> of one type is never the <c>T</c> of another;
/// members compare definition, declaring construction, and instantiation. Facts carried for
/// rendering play no part.
/// </remarks>
public static class SymbolIdentity
{
    /// <summary>
    /// True when the two types are the same.
    /// </summary>
    /// <param name="a">The first type.</param>
    /// <param name="b">The second type.</param>
    /// <returns>True when they match.</returns>
    public static bool Equal(TypeSymbol? a, TypeSymbol? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Kind != b.Kind)
        {
            return false;
        }

        return a.Kind switch
        {
            TypeSymbolKind.Primitive => a.Keyword == b.Keyword,
            TypeSymbolKind.Named => a.Definition == b.Definition,
            TypeSymbolKind.Constructed => Equal(a.Element, b.Element) && SequenceEqual(a.Arguments, b.Arguments),
            TypeSymbolKind.TypeParameter or TypeSymbolKind.MethodParameter => a.Owner == b.Owner && a.Position == b.Position,
            TypeSymbolKind.SzArray or TypeSymbolKind.ByRef or TypeSymbolKind.Pointer or TypeSymbolKind.Pinned => Equal(a.Element,
                b.Element),
            TypeSymbolKind.Array => a.Rank == b.Rank && Equal(a.Element, b.Element)
                && a.Sizes.SequenceEqual(b.Sizes) && a.LowerBounds.SequenceEqual(b.LowerBounds),
            TypeSymbolKind.FunctionPointer => Equal(a.Signature!, b.Signature!),
            TypeSymbolKind.Modified => a.IsRequired == b.IsRequired && Equal(a.Element, b.Element) && Equal(a.Modifier, b.Modifier),
            _ => false,
        };
    }

    /// <summary>
    /// True when the two signatures agree in convention, return type, and parameters.
    /// </summary>
    /// <param name="a">The first signature.</param>
    /// <param name="b">The second signature.</param>
    /// <returns>True when they match.</returns>
    public static bool Equal(MethodSignatureSymbol a, MethodSignatureSymbol b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.ManagedConvention == b.ManagedConvention
            && a.IsUnmanaged == b.IsUnmanaged
            && (!a.IsUnmanaged || a.UnmanagedConvention == b.UnmanagedConvention)
            && a.SentinelIndex == b.SentinelIndex
            && Equal(a.ReturnType, b.ReturnType)
            && SequenceEqual(a.Parameters, b.Parameters);
    }

    /// <summary>
    /// Compares a member's definition, declaring construction, and method arguments.
    /// </summary>
    /// <remarks>
    /// True when the two members are the same definition on the same declaring construction with
    /// the same generic arguments.
    /// </remarks>
    /// <param name="a">The first member.</param>
    /// <param name="b">The second member.</param>
    /// <returns>True when they match.</returns>
    public static bool Equal(MethodSymbol? a, MethodSymbol? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return a.Definition == b.Definition
            && a.Source == b.Source
            && Equal(a.DeclaringType, b.DeclaringType)
            && SequenceEqual(a.GenericArguments, b.GenericArguments);
    }

    /// <summary>
    /// True when the two fields are the same definition on the same declaring construction.
    /// </summary>
    /// <param name="a">The first field.</param>
    /// <param name="b">The second field.</param>
    /// <returns>True when they match.</returns>
    public static bool Equal(FieldSymbol? a, FieldSymbol? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return a.Definition == b.Definition && Equal(a.DeclaringType, b.DeclaringType);
    }

    /// <summary>
    /// True when two lists of types match pairwise.
    /// </summary>
    /// <param name="a">The first list.</param>
    /// <param name="b">The second list.</param>
    /// <returns>True when every pair matches.</returns>
    public static bool SequenceEqual(IReadOnlyList<TypeSymbol> a, IReadOnlyList<TypeSymbol> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!Equal(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A hash consistent with <see cref="Equal(TypeSymbol?, TypeSymbol?)"/>.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The hash.</returns>
    public static int Hash(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var hash = new HashCode();
        hash.Add(type.Kind);
        switch (type.Kind)
        {
            case TypeSymbolKind.Primitive:
                hash.Add(type.Keyword, StringComparer.Ordinal);
                break;
            case TypeSymbolKind.Named:
                hash.Add(type.Definition);
                break;
            case TypeSymbolKind.Constructed:
                hash.Add(Hash(type.Element!));
                foreach (var argument in type.Arguments)
                {
                    hash.Add(Hash(argument));
                }

                break;
            case TypeSymbolKind.TypeParameter:
            case TypeSymbolKind.MethodParameter:
                hash.Add(type.Owner);
                hash.Add(type.Position);
                break;
            case TypeSymbolKind.Array:
                hash.Add(type.Rank);
                hash.Add(Hash(type.Element!));
                hash.Add(type.Sizes.Count);
                foreach (var size in type.Sizes)
                {
                    hash.Add(size);
                }

                hash.Add(type.LowerBounds.Count);
                foreach (var bound in type.LowerBounds)
                {
                    hash.Add(bound);
                }

                break;
            case TypeSymbolKind.FunctionPointer:
                hash.Add(Hash(type.Signature!.ReturnType));
                hash.Add(type.Signature.Parameters.Count);
                break;
            case TypeSymbolKind.Modified:
                hash.Add(type.IsRequired);
                hash.Add(Hash(type.Element!));
                hash.Add(Hash(type.Modifier!));
                break;
            case TypeSymbolKind.Unresolved:
                hash.Add(type.Name, StringComparer.Ordinal);
                break;
            default:
                hash.Add(Hash(type.Element!));
                break;
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// A hash consistent with <see cref="Equal(MethodSymbol?, MethodSymbol?)"/>.
    /// </summary>
    /// <param name="method">The member.</param>
    /// <returns>The hash.</returns>
    public static int Hash(MethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var hash = new HashCode();
        hash.Add(method.Definition);
        hash.Add(method.Source);
        if (method.DeclaringType is not null)
        {
            hash.Add(Hash(method.DeclaringType));
        }

        foreach (var argument in method.GenericArguments)
        {
            hash.Add(Hash(argument));
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// A hash consistent with <see cref="Equal(FieldSymbol?, FieldSymbol?)"/>.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <returns>The hash.</returns>
    public static int Hash(FieldSymbol field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return HashCode.Combine(field.Definition, Hash(field.DeclaringType));
    }
}
