namespace IlRepl.Engine.Binding;

/// <summary>
/// Decides whether a type argument satisfies a generic parameter's constraints, by the CLI's
/// rules (ECMA-335 II.9.11) as the runtime applies them: the special constraints <c>class</c>,
/// <c>valuetype</c>, and <c>.ctor</c>, and the type constraints after substitution, with an
/// argument that is itself a generic parameter judged by what its own constraints promise.
/// </summary>
public static class GenericConstraints
{
    /// <summary>
    /// Checks a method's complete argument list after substituting its owner and method parameters into constraints.
    /// </summary>
    /// <param name="definition">The generic method definition.</param>
    /// <param name="declaring">The owner construction seen by the reference.</param>
    /// <param name="arguments">The supplied method arguments.</param>
    /// <param name="scope">The scope that knows their constraints.</param>
    /// <returns>Whether all arguments satisfy their corresponding slots.</returns>
    public static bool SatisfiesMethod(
        MethodSymbol definition, TypeSymbol? declaring, IReadOnlyList<TypeSymbol> arguments, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(scope);
        if (!definition.IsGenericDefinition || definition.Arity != arguments.Count)
        {
            return false;
        }

        TypeSymbol Substitute(TypeSymbol type)
        {
            var mapped = SymbolRelations.SubstituteMethodParameters(type, definition.Definition, arguments);
            return declaring is { Kind: TypeSymbolKind.Constructed } ? SymbolRelations.SubstituteFor(declaring, mapped) : mapped;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            if (!Satisfies(definition.GenericParameters[index], arguments[index], Substitute, scope))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the argument satisfies every constraint of the parameter.
    /// </summary>
    /// <param name="parameter">The parameter with its constraints.</param>
    /// <param name="argument">The argument.</param>
    /// <param name="substitute">Rewrites a constraint in terms of the arguments supplied for the owner's other parameters.</param>
    /// <param name="scope">The scope that knows bases, interfaces, and constructors.</param>
    /// <returns>True when the argument fits.</returns>
    public static bool Satisfies(GenericParameterSymbol parameter, TypeSymbol argument, Func<TypeSymbol, TypeSymbol> substitute, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(argument);
        ArgumentNullException.ThrowIfNull(substitute);
        ArgumentNullException.ThrowIfNull(scope);
        if (argument.Kind is TypeSymbolKind.ByRef or TypeSymbolKind.Pointer or TypeSymbolKind.FunctionPointer or TypeSymbolKind.Unresolved || SymbolIdentity.Equal(argument, TypeSymbol.Void))
        {
            return false;
        }

        if (argument.IsByRefLike && !parameter.Attributes.HasFlag(System.Reflection.GenericParameterAttributes.AllowByRefLike))
        {
            return false;
        }

        if (parameter.HasReferenceTypeConstraint && !IsReferenceType(argument, scope, []))
        {
            return false;
        }

        if (parameter.HasValueTypeConstraint && !IsNonNullableValueType(argument, scope, []))
        {
            return false;
        }

        if (parameter.HasDefaultConstructorConstraint && !HasDefaultConstructor(argument, scope, []))
        {
            return false;
        }

        foreach (var constraint in parameter.Constraints)
        {
            var target = substitute(constraint);
            if (!SatisfiesTypeConstraint(argument, target, scope, []))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the argument or its primary constraint proves it is a reference type.
    /// </summary>
    /// <param name="argument">The argument.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>True for a reference type.</returns>
    public static bool IsReferenceType(TypeSymbol argument, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(argument);
        ArgumentNullException.ThrowIfNull(scope);
        return IsReferenceType(argument, scope, []);
    }

    private static bool IsReferenceType(TypeSymbol argument, IBindingScope scope, HashSet<TypeSymbol> visiting)
    {
        if (argument.IsArray)
        {
            return true;
        }

        if (!argument.IsGenericParameter)
        {
            return argument.Kind is TypeSymbolKind.Named or TypeSymbolKind.Constructed or TypeSymbolKind.Primitive && !argument.IsValueTypeShape;
        }

        if (!visiting.Add(argument))
        {
            return false;
        }

        var declaration = scope.ParameterDeclaration(argument);
        if (declaration is null)
        {
            return false;
        }

        if (visiting.Count == 1 && declaration.HasReferenceTypeConstraint)
        {
            return true;
        }

        foreach (var constraint in declaration.Constraints)
        {
            if (constraint.IsGenericParameter)
            {
                if (IsReferenceType(constraint, scope, visiting))
                {
                    return true;
                }

                continue;
            }

            // A class constraint that is a reference type other than object proves the argument is one; an interface does not.
            if (constraint.Kind is TypeSymbolKind.Named or TypeSymbolKind.Constructed && !constraint.IsInterface && !constraint.IsValueTypeShape
                && SymbolRenderer.ReflectionFullName(constraint) is not ("System.Object" or "System.ValueType" or "System.Enum"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNonNullableValueType(TypeSymbol argument, IBindingScope scope, HashSet<TypeSymbol> visiting)
    {
        if (argument.IsGenericParameter)
        {
            if (!visiting.Add(argument))
            {
                return false;
            }

            var declaration = scope.ParameterDeclaration(argument);
            return declaration is not null && (declaration.HasValueTypeConstraint
                || declaration.Constraints.Any(constraint => IsNonNullableValueType(constraint, scope, visiting)));
        }

        if (!argument.IsValueTypeShape)
        {
            return false;
        }

        return !(argument.Kind == TypeSymbolKind.Constructed && SymbolRenderer.ReflectionFullName(argument.Element!) == "System.Nullable`1");
    }

    private static bool HasDefaultConstructor(TypeSymbol argument, IBindingScope scope, HashSet<TypeSymbol> visiting)
    {
        if (argument.IsGenericParameter)
        {
            if (!visiting.Add(argument))
            {
                return false;
            }

            var declaration = scope.ParameterDeclaration(argument);
            return declaration is not null
                && ((visiting.Count == 1 && declaration.HasDefaultConstructorConstraint) || declaration.HasValueTypeConstraint
                    || declaration.Constraints.Any(constraint => (constraint.IsGenericParameter || constraint.IsValueTypeShape)
                        && HasDefaultConstructor(constraint, scope, visiting)));
        }

        if (argument.IsValueTypeShape)
        {
            // MethodTable.HasExplicitOrImplicitPublicDefaultConstructor treats every value type as having one.
            return true;
        }

        if (argument.IsArray || argument.IsInterface || argument.IsAbstract)
        {
            return false;
        }

        return scope.Constructors(argument, false).Any(c => c.IsPublic && c.Parameters.Count == 0);
    }

    private static bool SatisfiesTypeConstraint(TypeSymbol argument, TypeSymbol target, IBindingScope scope, HashSet<TypeSymbol> visiting)
    {
        if (SymbolIdentity.Equal(argument, target))
        {
            return true;
        }

        if (target.HasUnresolved)
        {
            // Only the caller can defer constraints involving unfilled argument slots; missing metadata is never proof.
            return false;
        }

        if (!argument.IsGenericParameter)
        {
            return !target.IsGenericParameter && SymbolRelations.IsAssignable(argument, target, scope);
        }

        if (!visiting.Add(argument))
        {
            return false;
        }

        var declaration = scope.ParameterDeclaration(argument);
        if (declaration is null)
        {
            return false;
        }

        if (SymbolIdentity.Equal(target, TypeSymbol.Object))
        {
            return true;
        }

        if (declaration.HasValueTypeConstraint && SymbolRenderer.ReflectionFullName(target) == "System.ValueType")
        {
            return true;
        }

        foreach (var constraint in declaration.Constraints)
        {
            if (SatisfiesTypeConstraint(constraint, target, scope, visiting))
            {
                return true;
            }
        }

        return false;
    }
}
