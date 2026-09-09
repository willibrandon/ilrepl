namespace IlRepl.Engine.Binding;

/// <summary>
/// Rewrites and relates symbols structurally: substitution of generic parameters, and the
/// walks over constructions that every scope shares.
/// </summary>
public static partial class SymbolRelations
{
    /// <summary>
    /// Rewrites a type, replacing every node the function answers for and rebuilding constructed
    /// forms around the replaced parts.
    /// </summary>
    /// <param name="type">The type to rewrite.</param>
    /// <param name="replace">Returns the replacement for a node, or null to keep it.</param>
    /// <returns>The rewritten type, or the same symbol when nothing changed.</returns>
    public static TypeSymbol Rewrite(TypeSymbol type, Func<TypeSymbol, TypeSymbol?> replace)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(replace);
        if (replace(type) is { } replaced)
        {
            return replaced;
        }

        switch (type.Kind)
        {
            case TypeSymbolKind.Constructed:
            {
                var definition = Rewrite(type.Element!, replace);
                var changed = !ReferenceEquals(definition, type.Element);
                var arguments = new TypeSymbol[type.Arguments.Count];
                for (var i = 0; i < arguments.Length; i++)
                {
                    arguments[i] = Rewrite(type.Arguments[i], replace);
                    changed |= !ReferenceEquals(arguments[i], type.Arguments[i]);
                }

                return changed ? TypeSymbol.Construct(definition, arguments) : type;
            }

            case TypeSymbolKind.SzArray:
            {
                var element = Rewrite(type.Element!, replace);
                return ReferenceEquals(element, type.Element) ? type : TypeSymbol.SzArray(element);
            }

            case TypeSymbolKind.Array:
            {
                var element = Rewrite(type.Element!, replace);
                return ReferenceEquals(element, type.Element) ? type : TypeSymbol.Array(element, type.Rank, type.Sizes, type.LowerBounds);
            }

            case TypeSymbolKind.ByRef:
            {
                var element = Rewrite(type.Element!, replace);
                return ReferenceEquals(element, type.Element) ? type : TypeSymbol.ByRef(element);
            }

            case TypeSymbolKind.Pointer:
            {
                var element = Rewrite(type.Element!, replace);
                return ReferenceEquals(element, type.Element) ? type : TypeSymbol.Pointer(element);
            }

            case TypeSymbolKind.Modified:
            {
                var element = Rewrite(type.Element!, replace);
                var modifier = Rewrite(type.Modifier!, replace);
                return ReferenceEquals(element, type.Element) && ReferenceEquals(modifier, type.Modifier) ? type : TypeSymbol.Modified(element, modifier, type.IsRequired);
            }

            case TypeSymbolKind.Pinned:
            {
                var element = Rewrite(type.Element!, replace);
                return ReferenceEquals(element, type.Element) ? type : TypeSymbol.Pinned(element);
            }

            case TypeSymbolKind.FunctionPointer:
            {
                var signature = type.Signature!;
                var returnType = Rewrite(signature.ReturnType, replace);
                var parameters = signature.Parameters.Select(p => Rewrite(p, replace)).ToArray();
                var changed = !ReferenceEquals(returnType, signature.ReturnType) || parameters.Zip(signature.Parameters).Any(p => !ReferenceEquals(p.First, p.Second));
                return changed ? TypeSymbol.FunctionPointer(signature with { ReturnType = returnType, Parameters = parameters }) : type;
            }

            default:
                return type;
        }
    }

    /// <summary>
    /// Substitutes a definition's generic parameters with the arguments of a construction of it.
    /// </summary>
    /// <param name="type">The type to rewrite, written in terms of the definition's parameters.</param>
    /// <param name="definition">The generic definition whose parameters are replaced.</param>
    /// <param name="arguments">The arguments to put in their place.</param>
    /// <returns>The rewritten type.</returns>
    public static TypeSymbol SubstituteTypeParameters(TypeSymbol type, DefinitionId definition, IReadOnlyList<TypeSymbol> arguments)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(arguments);
        return Rewrite(type, t => t.Kind == TypeSymbolKind.TypeParameter && t.Owner == definition && t.Position < arguments.Count ? arguments[t.Position] : null);
    }

    /// <summary>
    /// Substitutes a method's own generic parameters with the arguments of a call.
    /// </summary>
    /// <param name="type">The type to rewrite, written in terms of the method's parameters.</param>
    /// <param name="method">The method definition whose parameters are replaced.</param>
    /// <param name="arguments">The arguments to put in their place.</param>
    /// <returns>The rewritten type.</returns>
    public static TypeSymbol SubstituteMethodParameters(TypeSymbol type, DefinitionId method, IReadOnlyList<TypeSymbol> arguments)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(arguments);
        return Rewrite(type, t => t.Kind == TypeSymbolKind.MethodParameter && t.Owner == method && t.Position < arguments.Count ? arguments[t.Position] : null);
    }

    /// <summary>
    /// Rewrites a type written in terms of a definition's parameters for one of its constructions.
    /// </summary>
    /// <param name="instantiation">The construction, or the definition itself.</param>
    /// <param name="type">A type mentioning the definition's parameters.</param>
    /// <returns>The rewritten type.</returns>
    public static TypeSymbol SubstituteFor(TypeSymbol instantiation, TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(instantiation);
        ArgumentNullException.ThrowIfNull(type);
        if (instantiation.Kind != TypeSymbolKind.Constructed)
        {
            return type;
        }

        return SubstituteTypeParameters(type, instantiation.Element!.Definition, instantiation.Arguments);
    }

    /// <summary>
    /// A member's signature seen through a declaring construction and a method instantiation.
    /// </summary>
    /// <param name="method">The member as declared.</param>
    /// <param name="declaring">The declaring construction, or the definition.</param>
    /// <param name="methodArguments">The method's generic arguments, or empty.</param>
    /// <returns>The member with both substitutions applied.</returns>
    public static MethodSymbol Instantiate(MethodSymbol method, TypeSymbol? declaring, IReadOnlyList<TypeSymbol> methodArguments)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(methodArguments);
        TypeSymbol Map(TypeSymbol t)
        {
            var mapped = declaring is not null ? SubstituteFor(declaring, t) : t;
            return methodArguments.Count > 0 ? SubstituteMethodParameters(mapped, method.Definition, methodArguments) : mapped;
        }

        return method.With(
            declaring ?? method.DeclaringType,
            Map(method.ReturnType),
            [.. method.Parameters.Select(p => p with { Type = Map(p.Type) })],
            methodArguments);
    }

    /// <summary>
    /// A field's type seen through a declaring construction.
    /// </summary>
    /// <param name="field">The field as declared.</param>
    /// <param name="declaring">The declaring construction, or the definition.</param>
    /// <returns>The field on that construction.</returns>
    public static FieldSymbol Instantiate(FieldSymbol field, TypeSymbol declaring)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(declaring);
        return field.With(declaring, SubstituteFor(declaring, field.FieldType));
    }

    /// <summary>
    /// The generic type definition of a construction, or the type itself.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The definition.</returns>
    public static TypeSymbol Definition(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.DefinitionOrSelf;
    }

    /// <summary>
    /// The outermost type enclosing a nested type, or the type itself.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The outermost definition.</returns>
    public static TypeSymbol Outermost(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var current = type.DefinitionOrSelf;
        while (current.Declaring is { } outer)
        {
            current = outer.DefinitionOrSelf;
        }

        return current;
    }

    /// <summary>
    /// True when <paramref name="inner"/> is <paramref name="outer"/> or is nested in it at any depth.
    /// </summary>
    /// <param name="inner">The candidate nested type.</param>
    /// <param name="outer">The enclosing type.</param>
    /// <returns>True when the nesting holds.</returns>
    public static bool IsWithin(TypeSymbol inner, TypeSymbol outer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(outer);
        var target = outer.DefinitionOrSelf;
        for (var current = inner.DefinitionOrSelf; current is not null; current = current.Declaring?.DefinitionOrSelf)
        {
            if (SymbolIdentity.Equal(current, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The element type under every array, byref, and pointer wrapper.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The innermost element.</returns>
    public static TypeSymbol InnermostElement(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var current = type;
        while (current.HasElement)
        {
            current = current.Element!;
        }

        return current;
    }
}
