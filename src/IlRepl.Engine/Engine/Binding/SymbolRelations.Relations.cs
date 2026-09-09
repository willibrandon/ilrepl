namespace IlRepl.Engine.Binding;

/// <summary>
/// How types relate, answered over symbols through a scope: the base chain, the interfaces, and
/// assignability, with the rules the runtime model applies to session types and the ones
/// reflection applies to loaded ones.
/// </summary>
public static partial class SymbolRelations
{
    private static readonly HashSet<string> VectorInterfaces = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.ICollection`1", "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IReadOnlyCollection`1", "System.Collections.Generic.IReadOnlyList`1",
    };

    private static readonly HashSet<string> ArrayInterfaces = new(StringComparer.Ordinal)
    {
        "System.Array", "System.ICloneable", "System.Collections.IList", "System.Collections.ICollection", "System.Collections.IEnumerable",
        "System.Collections.IStructuralComparable", "System.Collections.IStructuralEquatable",
    };

    /// <summary>
    /// Every interface the type implements: the declared ones, theirs, and the base chain's.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="scope">The scope that knows bases and interfaces.</param>
    /// <returns>The interfaces, without duplicates.</returns>
    public static IReadOnlyList<TypeSymbol> AllInterfacesOf(TypeSymbol type, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(scope);
        var found = new List<TypeSymbol>();
        var visited = new HashSet<TypeSymbol>();
        var pending = new Queue<TypeSymbol>();
        pending.Enqueue(type);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var implemented in scope.DeclaredInterfacesOf(current))
            {
                if (!found.Contains(implemented))
                {
                    found.Add(implemented);
                }

                pending.Enqueue(implemented);
            }

            if (!current.IsGenericParameter && scope.BaseOf(current) is { } parent)
            {
                pending.Enqueue(parent);
            }
        }

        return found;
    }

    /// <summary>
    /// True when <paramref name="derived"/> has <paramref name="baseType"/> somewhere in its base chain.
    /// </summary>
    /// <param name="derived">The candidate derived type.</param>
    /// <param name="baseType">The base type.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>True for a proper subclass.</returns>
    public static bool IsSubclassOf(TypeSymbol derived, TypeSymbol baseType, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(baseType);
        ArgumentNullException.ThrowIfNull(scope);
        var visited = new HashSet<TypeSymbol>();
        for (var current = scope.BaseOf(derived); current is not null && visited.Add(current); current = scope.BaseOf(current))
        {
            if (SymbolIdentity.Equal(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the definition of <paramref name="baseType"/> appears in the base chain of
    /// <paramref name="derived"/>, whatever the generic arguments.
    /// </summary>
    /// <param name="derived">The candidate derived type.</param>
    /// <param name="baseType">The base type.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>True when the definitions are related.</returns>
    public static bool IsSameOrSubclassDefinition(TypeSymbol derived, TypeSymbol baseType, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return IsSameOrSubclassDefinition(derived, baseType, scope.BaseOf);
    }

    /// <summary>
    /// True when the definition of <paramref name="baseType"/> appears in the base chain of
    /// <paramref name="derived"/>, with the base chain supplied as a function.
    /// </summary>
    /// <param name="derived">The candidate derived type.</param>
    /// <param name="baseType">The base type.</param>
    /// <param name="baseOf">The base type of a type, or null.</param>
    /// <returns>True when the definitions are related.</returns>
    public static bool IsSameOrSubclassDefinition(TypeSymbol derived, TypeSymbol baseType, Func<TypeSymbol, TypeSymbol?> baseOf)
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(baseType);
        ArgumentNullException.ThrowIfNull(baseOf);
        var target = baseType.DefinitionOrSelf;
        var visited = new HashSet<TypeSymbol>();
        for (var current = derived; current is not null && visited.Add(current); current = baseOf(current))
        {
            if (SymbolIdentity.Equal(current.DefinitionOrSelf, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a reference of type <paramref name="from"/> can stand where <paramref name="to"/>
    /// is expected: the same type, a base type, an implemented interface, or a variant
    /// instantiation of one, with the array rules the runtime applies.
    /// </summary>
    /// <param name="from">The type of the value.</param>
    /// <param name="to">The expected type.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>True when the assignment is valid.</returns>
    public static bool IsAssignable(TypeSymbol from, TypeSymbol to, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(scope);
        if (SymbolIdentity.Equal(from, to) || SymbolIdentity.Equal(to, TypeSymbol.Object))
        {
            return true;
        }

        if (from.IsGenericParameter || to.IsGenericParameter)
        {
            // Constraints settle these; the JIT checks the instantiation.
            return true;
        }

        if (from.Kind == TypeSymbolKind.Unresolved || to.Kind == TypeSymbolKind.Unresolved)
        {
            return false;
        }

        if (from.IsArray && to.IsArray)
        {
            var fromElement = from.Element!;
            var toElement = to.Element!;
            return from.Kind == to.Kind && from.Rank == to.Rank
                && !fromElement.IsValueTypeShape && !toElement.IsValueTypeShape && IsAssignable(fromElement, toElement, scope);
        }

        if (from.IsArray)
        {
            if (to.Kind == TypeSymbolKind.Named && ArrayInterfaces.Contains(SymbolRenderer.ReflectionFullName(to)))
            {
                return true;
            }

            // A vector implements the generic collection interfaces over its element type, and
            // over any reference type the element converts to, whatever the interface's own
            // variance: the runtime treats arrays specially here.
            if (!to.IsInterface || to.Kind != TypeSymbolKind.Constructed || from.Kind != TypeSymbolKind.SzArray || !VectorInterfaces.Contains(SymbolRenderer.ReflectionFullName(to.Element!)))
            {
                return false;
            }

            var element = from.Element!;
            var wanted = to.Arguments[0];
            return SymbolIdentity.Equal(element, wanted) || (!element.IsValueTypeShape && !wanted.IsValueTypeShape && IsAssignable(element, wanted, scope));
        }

        if (from.HasElement || to.HasElement || from.Kind == TypeSymbolKind.FunctionPointer || to.Kind == TypeSymbolKind.FunctionPointer)
        {
            return false;
        }

        if (to.Kind == TypeSymbolKind.Constructed && SymbolRenderer.ReflectionFullName(to.Element!) == "System.Nullable`1" && SymbolIdentity.Equal(to.Arguments[0], from))
        {
            return true;
        }

        // A construction converts to a variant construction of its own definition, interface or delegate alike.
        if (IsVariantMatch(from, to, scope))
        {
            return true;
        }

        if (to.IsInterface)
        {
            return AllInterfacesOf(from, scope).Any(i => IsVariantMatch(i, to, scope));
        }

        for (var current = scope.BaseOf(from); current is not null; current = scope.BaseOf(current))
        {
            if (IsVariantMatch(current, to, scope))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVariantMatch(TypeSymbol candidate, TypeSymbol target, IBindingScope scope)
    {
        if (SymbolIdentity.Equal(candidate, target))
        {
            return true;
        }

        if (candidate.Kind != TypeSymbolKind.Constructed || target.Kind != TypeSymbolKind.Constructed || !SymbolIdentity.Equal(candidate.Element, target.Element))
        {
            return false;
        }

        var parameters = scope.GenericParameterDeclarations(target.Element!);
        for (var i = 0; i < target.Arguments.Count; i++)
        {
            var a = candidate.Arguments[i];
            var b = target.Arguments[i];
            if (SymbolIdentity.Equal(a, b))
            {
                continue;
            }

            if (a.IsValueTypeShape || b.IsValueTypeShape || a.IsGenericParameter || b.IsGenericParameter)
            {
                return false;
            }

            var variance = i < parameters.Count ? parameters[i].Attributes & System.Reflection.GenericParameterAttributes.VarianceMask : System.Reflection.GenericParameterAttributes.None;
            var ok = variance switch
            {
                System.Reflection.GenericParameterAttributes.Covariant => IsAssignable(a, b, scope),
                System.Reflection.GenericParameterAttributes.Contravariant => IsAssignable(b, a, scope),
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
