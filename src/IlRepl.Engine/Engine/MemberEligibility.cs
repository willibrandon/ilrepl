using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// The accessibility rules over symbols, for every source alike. A session member is judged by the
/// rules <see cref="MemberAccess"/> teaches, which this class now holds; a member of another
/// assembly is judged by what a cell, which the runtime does check, can reach: public members,
/// family members from a derived session type, and nothing internal to that assembly.
/// </summary>
public static class MemberEligibility
{
    /// <summary>
    /// Decides whether a session type is visible from a context: null when it is, otherwise the
    /// reason. Element types and generic arguments are judged too. Other types are not judged
    /// unless <paramref name="judgeAll"/> is set.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="where">Where the mention happens.</param>
    /// <param name="facts">The base chain, the session's types, and the spelling.</param>
    /// <param name="judgeAll">True to judge types of any assembly by the session rules, as the equivalence harness does.</param>
    /// <returns>The reason, or null.</returns>
    public static string? TypeVerdict(TypeSymbol type, AccessContext where, AccessFacts facts, bool judgeAll = false)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(facts);
        if (type.IsGenericParameter)
        {
            return null;
        }

        while (type.HasElement)
        {
            type = type.Element!;
            if (type.IsGenericParameter)
            {
                return null;
            }
        }

        if (type.Kind == TypeSymbolKind.Constructed)
        {
            foreach (var argument in type.Arguments)
            {
                if (TypeVerdict(argument, where, facts, judgeAll) is { } problem)
                {
                    return problem;
                }
            }
        }

        var definition = type.DefinitionOrSelf;
        if (definition.Kind != TypeSymbolKind.Named)
        {
            return null;
        }

        if (!judgeAll && !facts.IsSessionType(definition))
        {
            return null;
        }

        if (definition.Declaring is null)
        {
            // A top-level session type is visible throughout the session, public or not.
            return null;
        }

        var enclosing = definition.Declaring;
        if (TypeVerdict(enclosing, where, facts, judgeAll) is { } outerProblem)
        {
            return outerProblem;
        }

        var name = facts.Pretty(definition);
        var outerName = facts.Pretty(enclosing);
        switch (definition.Attributes & TypeAttributes.VisibilityMask)
        {
            case TypeAttributes.NestedPublic:
            case TypeAttributes.NestedAssembly:
            case TypeAttributes.NestedFamORAssem:
                return null;
            case TypeAttributes.NestedPrivate:
                return where.Type is not null && SymbolRelations.IsWithin(where.Type, enclosing)
                    ? null
                    : $"{name} is nested private; only {outerName} and the types nested in it can use it, not {where.Description}";
            default:
                return FamilyAccessor(where.Type, enclosing, facts) is not null
                    ? null
                    : $"{name} is {MemberAccess.VisibilityWord(definition.Attributes)}; only {outerName} and types derived from it can use it, not {where.Description}";
        }
    }

    /// <summary>
    /// Decides a member access by its access word: null when allowed, otherwise the reason.
    /// </summary>
    /// <param name="access">The ILAsm access word.</param>
    /// <param name="declaring">The declaring type.</param>
    /// <param name="description">How the message names the member.</param>
    /// <param name="where">Where the access happens.</param>
    /// <param name="facts">The base chain, the session's types, and the spelling.</param>
    /// <returns>The reason, or null.</returns>
    public static string? MemberVerdict(string access, TypeSymbol declaring, string description, AccessContext where, AccessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(declaring);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(facts);
        var owner = facts.Pretty(declaring);
        return access switch
        {
            "public" or "assembly" or "famorassem" => null,
            "private" => where.Type is not null && SymbolRelations.IsWithin(where.Type, declaring)
                ? null
                : $"{description} is private; only {owner} and the types nested in it can use it, not {where.Description}",
            "privatescope" => where.Type is not null && SymbolIdentity.Equal(SymbolRelations.Outermost(where.Type), SymbolRelations.Outermost(declaring))
                ? null
                : $"{description} is privatescope (no access word); only {owner}'s own module can use it, not {where.Description} (give it an access word such as public)",
            _ => FamilyAccessor(where.Type, declaring, facts) is not null
                ? null
                : $"{description} is {access}; only {owner} and types derived from it can use it, not {where.Description}",
        };
    }

    /// <summary>
    /// Decides a field access: null when allowed, otherwise the reason. Only session fields are
    /// judged unless <paramref name="judgeAll"/> is set.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="where">Where the access happens.</param>
    /// <param name="facts">The base chain, the session's types, and the spelling.</param>
    /// <param name="judgeAll">True to judge fields of any assembly by the session rules.</param>
    /// <returns>The reason, or null.</returns>
    public static string? FieldVerdict(FieldSymbol field, AccessContext where, AccessFacts facts, bool judgeAll = false)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(facts);
        var declaring = field.DeclaringType;
        if (!judgeAll && !facts.IsSessionType(declaring))
        {
            return null;
        }

        var description = $"{facts.Pretty(field.FieldType)} {facts.Pretty(declaring)}::{field.Name}";
        return TypeVerdict(declaring, where, facts, judgeAll) ?? MemberVerdict(MemberAccess.AccessWord(field.Attributes), declaring, description, where, facts);
    }

    /// <summary>
    /// Decides a method access: null when allowed, otherwise the reason. The declaring type and
    /// the instantiation's arguments are judged whatever assembly the method belongs to; the
    /// member's own access is a session member's, unless <paramref name="judgeAll"/> is set.
    /// </summary>
    /// <param name="method">The member.</param>
    /// <param name="where">Where the access happens.</param>
    /// <param name="facts">The base chain, the session's types, and the spelling.</param>
    /// <param name="judgeAll">True to judge methods of any assembly by the session rules.</param>
    /// <returns>The reason, or null.</returns>
    public static string? MethodVerdict(MethodSymbol method, AccessContext where, AccessFacts facts, bool judgeAll = false)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(facts);
        if (method.Source == MethodSymbolSource.Session || method.DeclaringType is null)
        {
            return null;
        }

        var declaring = method.DeclaringType;
        if (TypeVerdict(declaring, where, facts, judgeAll) is { } declaringProblem)
        {
            return declaringProblem;
        }

        foreach (var argument in method.GenericArguments)
        {
            if (TypeVerdict(argument, where, facts, judgeAll) is { } argumentProblem)
            {
                return argumentProblem;
            }
        }

        if (!judgeAll && !facts.IsSessionType(declaring))
        {
            return null;
        }

        var description = method.Source is MethodSymbolSource.Declared or MethodSymbolSource.Forward
            ? $"{SymbolRenderer.DescribeMember(method, facts.Pretty)} on {facts.Pretty(declaring)}"
            : SymbolRenderer.Describe(method, facts.Pretty);
        return MemberVerdict(MemberAccess.AccessWord(method.Attributes), declaring, description, where, facts);
    }

    /// <summary>
    /// The type in the context, or one enclosing it, that is the declaring type or derives from it.
    /// </summary>
    /// <param name="scopeType">The context's type, or null.</param>
    /// <param name="declaring">The declaring type.</param>
    /// <param name="facts">The base chain.</param>
    /// <returns>The accessor, or null.</returns>
    public static TypeSymbol? FamilyAccessor(TypeSymbol? scopeType, TypeSymbol declaring, AccessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        ArgumentNullException.ThrowIfNull(facts);
        for (var current = scopeType; current is not null; current = current.DefinitionOrSelf.Declaring)
        {
            if (SymbolRelations.IsSameOrSubclassDefinition(current, declaring, facts.BaseOf))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// True when a type of another assembly is reachable from a context: its element type is, each
    /// generic argument is, and it is public, or nested public in a reachable type, or nested
    /// family in a type the context derives from. Session types answer through <see cref="TypeVerdict"/>.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="where">Where the mention happens.</param>
    /// <param name="facts">The base chain, the session's types, and the spelling.</param>
    /// <returns>True when the type is reachable.</returns>
    public static bool IsReachable(TypeSymbol type, AccessContext where, AccessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(facts);
        if (type.IsGenericParameter || type.Kind == TypeSymbolKind.Primitive)
        {
            return true;
        }

        if (type.Kind == TypeSymbolKind.Unresolved)
        {
            return false;
        }

        if (type.HasElement)
        {
            return IsReachable(type.Element!, where, facts);
        }

        if (type.Kind == TypeSymbolKind.FunctionPointer)
        {
            return IsReachable(type.Signature!.ReturnType, where, facts) && type.Signature.Parameters.All(p => IsReachable(p, where, facts));
        }

        if (type.Kind == TypeSymbolKind.Constructed && !type.Arguments.All(a => IsReachable(a, where, facts)))
        {
            return false;
        }

        var definition = type.DefinitionOrSelf;
        if (facts.IsSessionType(definition))
        {
            return TypeVerdict(definition, where, facts) is null;
        }

        var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
        if (definition.Declaring is null)
        {
            return visibility == TypeAttributes.Public;
        }

        if (!IsReachable(definition.Declaring, where, facts))
        {
            return false;
        }

        return visibility switch
        {
            TypeAttributes.NestedPublic => true,
            TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem => FamilyAccessor(where.Type, definition.Declaring, facts) is not null,
            _ => false,
        };
    }

    /// <summary>
    /// The reason a member cannot be used from a context, or null when it can: a session member by
    /// the session's rules, a member of another assembly by what a cell can reach.
    /// </summary>
    /// <param name="method">The member.</param>
    /// <param name="where">Where the access happens.</param>
    /// <param name="facts">The base chain, the session's types, and the spelling.</param>
    /// <returns>The reason, or null.</returns>
    public static string? AccessProblem(MethodSymbol method, AccessContext where, AccessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(facts);
        if (method.Source == MethodSymbolSource.Session || method.DeclaringType is null)
        {
            return null;
        }

        if (facts.IsSessionType(method.DeclaringType))
        {
            return MethodVerdict(method, where, facts);
        }

        if (!IsReachable(method.DeclaringType, where, facts) || !method.GenericArguments.All(a => IsReachable(a, where, facts)))
        {
            return $"{SymbolRenderer.Describe(method, facts.Pretty)} is not reachable from {where.Description}";
        }

        var access = method.Attributes & MethodAttributes.MemberAccessMask;
        var reachable = access == MethodAttributes.Public
            || (access is MethodAttributes.Family or MethodAttributes.FamORAssem && FamilyAccessor(where.Type, method.DeclaringType, facts) is not null);
        return reachable ? null : $"{SymbolRenderer.Describe(method, facts.Pretty)} is {MemberAccess.AccessWord(method.Attributes)} to its own assembly, not {where.Description}";
    }

    /// <summary>
    /// The reason a field cannot be used from a context, or null when it can.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="where">Where the access happens.</param>
    /// <param name="facts">The base chain, the session's types, and the spelling.</param>
    /// <returns>The reason, or null.</returns>
    public static string? AccessProblem(FieldSymbol field, AccessContext where, AccessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.IsSessionType(field.DeclaringType))
        {
            return FieldVerdict(field, where, facts);
        }

        var description = $"{facts.Pretty(field.FieldType)} {facts.Pretty(field.DeclaringType)}::{field.Name}";
        if (!IsReachable(field.DeclaringType, where, facts) || !IsReachable(field.FieldType, where, facts))
        {
            return $"{description} is not reachable from {where.Description}";
        }

        var access = field.Attributes & FieldAttributes.FieldAccessMask;
        var reachable = access == FieldAttributes.Public
            || (access is FieldAttributes.Family or FieldAttributes.FamORAssem && FamilyAccessor(where.Type, field.DeclaringType, facts) is not null);
        return reachable ? null : $"{description} is {MemberAccess.AccessWord(field.Attributes)} to its own assembly, not {where.Description}";
    }

    /// <summary>
    /// The reason a type cannot be mentioned from a context, or null when it can.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="where">Where the mention happens.</param>
    /// <param name="facts">The base chain, the session's types, and the spelling.</param>
    /// <returns>The reason, or null.</returns>
    public static string? AccessProblem(TypeSymbol type, AccessContext where, AccessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(facts);
        if (TypeVerdict(type, where, facts) is { } problem)
        {
            return problem;
        }

        return IsReachable(type, where, facts) ? null : $"{facts.Pretty(type)} is not reachable from {where.Description}";
    }
}
