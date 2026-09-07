using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Answers questions about how types relate when some of them are session types: the base
/// chain, the interfaces, and assignability. Reflection answers for framework types; a type
/// being written answers through the declarations kept in the <see cref="TypeTable"/>, because
/// its builder cannot describe itself before it is created.
/// </summary>
public static class TypeRelations
{
    /// <summary>
    /// True for a type the session declared: a prototype still being written or a loaded
    /// session type. Constructed forms are judged by their definition.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>True for a session type.</returns>
    public static bool IsSessionType(Type? type)
    {
        if (type is null || type.IsGenericParameter)
        {
            return false;
        }

        while (type.HasElementType)
        {
            type = type.GetElementType()!;
        }

        var definition = Definition(type);
        return definition is TypeBuilder || SessionAssemblies.IsSessionType(definition);
    }

    /// <summary>
    /// The generic type definition of a constructed type, or the type itself.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The definition.</returns>
    public static Type Definition(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;
    }

    /// <summary>
    /// The outermost type enclosing a nested type, or the type itself.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The outermost type.</returns>
    public static Type Outermost(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var current = Definition(type);
        while (current.DeclaringType is { } outer)
        {
            current = Definition(outer);
        }

        return current;
    }

    /// <summary>
    /// True when <paramref name="inner"/> is <paramref name="outer"/> or is nested in it at any depth.
    /// </summary>
    /// <param name="inner">The candidate nested type.</param>
    /// <param name="outer">The enclosing type.</param>
    /// <returns>True when the nesting holds.</returns>
    public static bool IsWithin(Type inner, Type outer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(outer);
        var target = Definition(outer);
        for (Type? current = Definition(inner); current is not null; current = current.DeclaringType is { } d ? Definition(d) : null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The base type, with the type's generic arguments substituted into it; null for object
    /// and interfaces.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <returns>The base type.</returns>
    public static Type? BaseTypeOf(Type type, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(types);
        if (type.IsGenericParameter)
        {
            return type.BaseType ?? typeof(object);
        }

        var definition = Definition(type);
        Type? baseType = types.TryGetMembers(definition, out var own) ? own.BaseType : definition.BaseType;
        return baseType is null ? null : SubstituteFor(type, baseType);
    }

    /// <summary>
    /// The interfaces the type declares, with its generic arguments substituted into them.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <returns>The declared interfaces.</returns>
    public static IReadOnlyList<Type> DeclaredInterfacesOf(Type type, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(types);
        if (type.IsGenericParameter)
        {
            return type.GetGenericParameterConstraints().Where(c => c.IsInterface).ToArray();
        }

        var definition = Definition(type);
        IReadOnlyList<Type> declared;
        if (types.TryGetMembers(definition, out var own))
        {
            declared = own.Interfaces;
        }
        else if (definition is TypeBuilder)
        {
            declared = [];
        }
        else
        {
            declared = definition.GetInterfaces();
        }

        return declared.Select(i => SubstituteFor(type, i)).ToArray();
    }

    /// <summary>
    /// Every interface the type implements: the declared ones, theirs, and the base chain's.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <returns>The interfaces, without duplicates.</returns>
    public static IReadOnlyList<Type> AllInterfacesOf(Type type, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(types);
        var found = new List<Type>();
        var seen = new HashSet<Type>(ReferenceEqualityComparer.Instance);
        void Visit(Type t)
        {
            foreach (var i in DeclaredInterfacesOf(t, types))
            {
                if (seen.Add(i) || !found.Any(f => TypeIdentity.Equal(f, i)))
                {
                    if (!found.Any(f => TypeIdentity.Equal(f, i)))
                    {
                        found.Add(i);
                    }

                    Visit(i);
                }
            }
        }

        for (var current = type; current is not null; current = BaseTypeOf(current, types))
        {
            Visit(current);
            if (current.IsGenericParameter)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// True when <paramref name="derived"/> has <paramref name="baseType"/> somewhere in its base chain.
    /// </summary>
    /// <param name="derived">The candidate derived type.</param>
    /// <param name="baseType">The base type.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <returns>True for a proper subclass.</returns>
    public static bool IsSubclassOf(Type derived, Type baseType, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(baseType);
        ArgumentNullException.ThrowIfNull(types);
        for (var current = BaseTypeOf(derived, types); current is not null; current = BaseTypeOf(current, types))
        {
            if (TypeIdentity.Equal(current, baseType))
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
    /// <param name="types">The table that knows the types being written.</param>
    /// <returns>True when the definitions are related.</returns>
    public static bool IsSameOrSubclassDefinition(Type derived, Type baseType, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(baseType);
        ArgumentNullException.ThrowIfNull(types);
        var target = Definition(baseType);
        for (var current = derived; current is not null; current = BaseTypeOf(current, types))
        {
            if (ReferenceEquals(Definition(current), target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a reference of type <paramref name="from"/> can stand where <paramref name="to"/>
    /// is expected: the same type, a base type, an implemented interface, or a variant
    /// instantiation of one. Framework types answer through reflection; session types walk
    /// their declarations.
    /// </summary>
    /// <param name="from">The type of the value.</param>
    /// <param name="to">The expected type.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <returns>True when the assignment is valid.</returns>
    public static bool IsAssignable(Type from, Type to, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(types);
        if (TypeIdentity.Equal(from, to) || to == typeof(object))
        {
            return true;
        }

        if (from.IsGenericParameter || to.IsGenericParameter)
        {
            // Constraints settle these; the JIT checks the instantiation.
            return true;
        }

        if (!IsSessionType(from) && !IsSessionType(to))
        {
            return to.IsAssignableFrom(from);
        }

        if (from.IsArray && to.IsArray)
        {
            var fromElement = from.GetElementType()!;
            var toElement = to.GetElementType()!;
            return from.IsSZArray == to.IsSZArray && from.GetArrayRank() == to.GetArrayRank()
                && !fromElement.IsValueType && !toElement.IsValueType && IsAssignable(fromElement, toElement, types);
        }

        if (from.IsArray)
        {
            return to.IsAssignableFrom(typeof(Array)) || (to.IsInterface && to.IsGenericType && typeof(Array).GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == to.GetGenericTypeDefinition()) && from.IsSZArray && IsVariantMatch(to.GetGenericTypeDefinition().MakeGenericType(from.GetElementType()!), to, types));
        }

        if (from.HasElementType || to.HasElementType)
        {
            return false;
        }

        if (to.IsInterface)
        {
            return AllInterfacesOf(from, types).Any(i => IsVariantMatch(i, to, types));
        }

        for (var current = BaseTypeOf(from, types); current is not null; current = BaseTypeOf(current, types))
        {
            if (IsVariantMatch(current, to, types))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Substitutes a definition's generic parameters with the arguments of a constructed type.
    /// </summary>
    /// <param name="type">The type to rewrite.</param>
    /// <param name="definitionArguments">The definition's generic parameters.</param>
    /// <param name="actualArguments">The arguments to put in their place.</param>
    /// <returns>The rewritten type.</returns>
    public static Type Substitute(Type type, IReadOnlyList<Type> definitionArguments, IReadOnlyList<Type> actualArguments)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(definitionArguments);
        ArgumentNullException.ThrowIfNull(actualArguments);
        if (type.IsGenericParameter && type.DeclaringMethod is null)
        {
            for (var i = 0; i < definitionArguments.Count; i++)
            {
                if (ReferenceEquals(definitionArguments[i], type))
                {
                    return actualArguments[i];
                }
            }

            return type;
        }

        if (type.IsByRef)
        {
            return Substitute(type.GetElementType()!, definitionArguments, actualArguments).MakeByRefType();
        }

        if (type.IsPointer)
        {
            return Substitute(type.GetElementType()!, definitionArguments, actualArguments).MakePointerType();
        }

        if (type.IsArray)
        {
            var element = Substitute(type.GetElementType()!, definitionArguments, actualArguments);
            return type.IsSZArray ? element.MakeArrayType() : element.MakeArrayType(type.GetArrayRank());
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var args = type.GetGenericArguments().Select(a => Substitute(a, definitionArguments, actualArguments)).ToArray();
            return type.GetGenericTypeDefinition().MakeGenericType(args);
        }

        return type;
    }

    /// <summary>
    /// Rewrites a type written in terms of a definition's parameters for one of its instantiations.
    /// </summary>
    /// <param name="instantiation">The constructed type, or the definition itself.</param>
    /// <param name="type">A type mentioning the definition's parameters.</param>
    /// <returns>The rewritten type.</returns>
    public static Type SubstituteFor(Type instantiation, Type type)
    {
        ArgumentNullException.ThrowIfNull(instantiation);
        ArgumentNullException.ThrowIfNull(type);
        if (!instantiation.IsGenericType || instantiation.IsGenericTypeDefinition)
        {
            return type;
        }

        return Substitute(type, instantiation.GetGenericTypeDefinition().GetGenericArguments(), instantiation.GetGenericArguments());
    }

    private static bool IsVariantMatch(Type candidate, Type target, TypeTable types)
    {
        if (TypeIdentity.Equal(candidate, target))
        {
            return true;
        }

        if (!candidate.IsGenericType || !target.IsGenericType || candidate.IsGenericTypeDefinition || target.IsGenericTypeDefinition
            || !ReferenceEquals(Definition(candidate), Definition(target)))
        {
            return false;
        }

        var parameters = Definition(target).GetGenericArguments();
        var candidateArguments = candidate.GetGenericArguments();
        var targetArguments = target.GetGenericArguments();
        for (var i = 0; i < parameters.Length; i++)
        {
            var variance = parameters[i].GenericParameterAttributes & GenericParameterAttributes.VarianceMask;
            var a = candidateArguments[i];
            var b = targetArguments[i];
            if (TypeIdentity.Equal(a, b))
            {
                continue;
            }

            if (a.IsValueType || b.IsValueType || a.IsGenericParameter || b.IsGenericParameter)
            {
                return false;
            }

            var ok = variance switch
            {
                GenericParameterAttributes.Covariant => IsAssignable(a, b, types),
                GenericParameterAttributes.Contravariant => IsAssignable(b, a, types),
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
