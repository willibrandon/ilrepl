using System.Reflection;
using System.Reflection.Emit;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Engine;

public static partial class MemberEligibility
{
    /// <summary>
    /// Filters methods by the instruction or directive without requiring a complete evaluation stack.
    /// </summary>
    /// <param name="method">The candidate method.</param>
    /// <param name="site">The operand being edited.</param>
    /// <param name="view">The captured body and accessibility context.</param>
    /// <returns>Whether this site can offer the method.</returns>
    public static bool Admits(MethodSymbol method, CompletionSite site, EditingView view)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(view);
        if (site.Owner is ".dis" or ".disassemble")
        {
            return Inspectable(method);
        }

        var scope = view.Scope;
        if (AccessProblem(method, scope.Access, AccessFacts.From(scope)) is not null)
        {
            return false;
        }

        if (method.Source == MethodSymbolSource.Session && site.ExplicitInstance)
        {
            return false;
        }

        return site.Owner switch
        {
            "call" => method.IsStatic ? !method.IsVirtual || HasConstrainedInterfaceReceiver(method, view) : !method.IsAbstract,
            "callvirt" => !method.IsStatic && !method.IsConstructor,
            "newobj" => method.Name == ".ctor" && method.DeclaringType is { IsAbstract: false },
            ".custom" => method.Name == ".ctor" && method.DeclaringType is { IsAbstract: false } owner
                && HasBase(owner, "System.Attribute", scope),
            "ldftn" => !method.IsAbstract,
            "jmp" => !method.IsAbstract && IsJumpTarget(method, view),
            "ldvirtftn" => !method.IsStatic && method.IsVirtual && !method.IsConstructor,
            "ldtoken method" => true,
            ".override" => IsOverrideTarget(method, view),
            ".override with" or ".get" or ".set" or ".other" or ".addon" or ".removeon" or ".fire"
                => method.IsDeclared && !method.IsConstructor
                    && SymbolIdentity.Equal(method.DeclaringType?.DefinitionOrSelf, view.Owner?.DefinitionOrSelf),
            _ => false,
        };
    }

    /// <summary>
    /// Filters fields by storage kind and the same initonly store rule used by submission.
    /// </summary>
    /// <param name="field">The candidate field.</param>
    /// <param name="site">The operand being edited.</param>
    /// <param name="view">The captured body and accessibility context.</param>
    /// <returns>Whether this site can offer the field.</returns>
    public static bool Admits(FieldSymbol field, CompletionSite site, EditingView view)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(view);
        if (AccessProblem(field, view.Scope.Access, AccessFacts.From(view.Scope)) is not null)
        {
            return false;
        }

        var storage = site.Owner switch
        {
            "ldsfld" or "ldsflda" or "stsfld" => field.IsStatic && !field.IsLiteral,
            "ldfld" or "ldflda" or "stfld" => !field.IsStatic,
            "ldtoken field" => true,
            _ => false,
        };
        return storage && InstructionMemberRules.InitOnlyStoreProblem(field, site.Owner, view.OpenMethod,
            view.Scope.Access.Type, view.StackKind != AnalyzedStackKind.Known || view.IsThisAt(view.Stack.Count - 2),
            view.Scope.Pretty) is null;
    }

    /// <summary>
    /// Applies type-site restrictions without excluding kinds that IL legally permits as tokens or element types.
    /// </summary>
    /// <param name="type">The candidate type.</param>
    /// <param name="site">The operand being edited.</param>
    /// <param name="view">The captured declaration and accessibility context.</param>
    /// <param name="hasElementSuffix">Whether an existing suffix makes this the element of a compound type.</param>
    /// <returns>Whether the type is eligible before the completed declaration is bound.</returns>
    public static bool Admits(TypeSymbol type, CompletionSite site, EditingView view, bool hasElementSuffix = false)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(view);
        if (type.HasUnresolved)
        {
            return false;
        }

        var scope = view.Scope;
        if (site.Owner is not (".dis" or ".disassemble") && AccessProblem(type, scope.Access, AccessFacts.From(scope)) is not null)
        {
            return false;
        }

        if (site.Kind == CompletionSiteKind.TypeArgument)
        {
            // Generic constraints judge the argument; declaration rules judge the completed enclosing type.
            return true;
        }

        var variableSite = site.Owner is ".locals" or ".args" or ".field" or ".property"
            || site.Owner == ".method" && site.ArgumentIndex >= 0;
        if (variableSite && !site.IsFunctionPointerReturn && !hasElementSuffix && !IsVariableType(type))
        {
            return false;
        }

        if (site.Kind is CompletionSiteKind.Type or CompletionSiteKind.GenericParameter
            && !hasElementSuffix && !site.IsFunctionPointerReturn && !IsInstructionType(type, site.Owner))
        {
            return false;
        }

        if (site.Owner == ".args" && ContainsParameter(type))
        {
            return false;
        }

        return site.Owner switch
        {
            "extends" => view.OwnerKind == TypeKind.Struct ? IsCoreType(type, "System.ValueType", scope)
                : view.OwnerKind == TypeKind.Enum ? IsCoreType(type, "System.Enum", scope)
                : view.OwnerKind != TypeKind.Interface && !type.IsInterface && !type.IsValueTypeShape && !type.IsSealed
                    && type.Unwrapped.Kind is TypeSymbolKind.Named or TypeSymbolKind.Constructed or TypeSymbolKind.Primitive,
            "implements" => view.OwnerKind != TypeKind.Enum && type.IsInterface,
            ".event" => HasBase(type, "System.MulticastDelegate", scope),
            _ => true,
        };
    }

    /// <summary>
    /// Checks body availability without opening a runtime method body or creating a declaration.
    /// </summary>
    /// <param name="method">The candidate method.</param>
    /// <returns>Whether a listing can inspect the retained method body.</returns>
    public static bool Inspectable(MethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method.IsDeclared && method.Source != MethodSymbolSource.Forward && method.HasIlBody;
    }

    private static bool IsJumpTarget(MethodSymbol method, EditingView view)
    {
        var source = view.OpenMethod;
        var convention = source?.CallingConvention
            ?? (view.IsVarArg ? CallingConventions.VarArgs : CallingConventions.Standard);
        var parameters = source?.Parameters
            ?? view.Scope.Arguments.Select(argument => new ParameterSymbol(argument.Type, argument.Name)).ToArray();
        if (method.IsStatic != (source?.IsStatic ?? true) || method.CallingConvention != convention
            || method.Arity != view.Scope.Generics.MethodArguments.Count || method.Parameters.Count != parameters.Count)
        {
            return false;
        }

        // Generic starters may acquire compatible arguments later. Match their parameter shapes
        // consistently now, then compare the actual instantiation when its signature is complete.
        var substitutions = method.IsGenericDefinition ? new Dictionary<int, TypeSymbol>() : null;
        bool Match(TypeSymbol target, TypeSymbol current) => substitutions is null ? SymbolIdentity.Equal(target, current)
            : MatchJumpType(target, current, method.Definition, substitutions);
        bool MatchList(IReadOnlyList<TypeSymbol> targets, IReadOnlyList<TypeSymbol> current) => targets.Count == current.Count
            && targets.Zip(current).All(pair => Match(pair.First, pair.Second));
        if (!Match(method.ReturnType, source?.ReturnType ?? TypeSymbol.Object)
            || !MatchList(method.ReturnRequiredModifiers, source?.ReturnRequiredModifiers ?? [])
            || !MatchList(method.ReturnOptionalModifiers, source?.ReturnOptionalModifiers ?? [])
            || !method.Parameters.Zip(parameters).All(pair => Match(pair.First.Type, pair.Second.Type)
                && MatchList(pair.First.RequiredModifiers, pair.Second.RequiredModifiers)
                && MatchList(pair.First.OptionalModifiers, pair.Second.OptionalModifiers)))
        {
            return false;
        }

        if (!method.IsStatic)
        {
            var receiver = view.Scope.Arguments.Count == 0 ? null : view.Scope.Arguments[0].Type;
            var owner = method.DeclaringType!;
            return receiver is not null && (owner.IsValueTypeShape
                ? SymbolIdentity.Equal(receiver, TypeSymbol.ByRef(owner))
                : SymbolRelations.IsAssignable(receiver, owner, view.Scope));
        }

        return true;
    }

    private static bool MatchJumpType(TypeSymbol target, TypeSymbol current, DefinitionId definition,
        Dictionary<int, TypeSymbol> substitutions)
    {
        if (target.Kind == TypeSymbolKind.MethodParameter && target.Owner == definition)
        {
            if (substitutions.TryGetValue(target.Position, out var previous))
            {
                return SymbolIdentity.Equal(previous, current);
            }

            substitutions.Add(target.Position, current);
            return true;
        }

        bool Match(TypeSymbol first, TypeSymbol second) => MatchJumpType(first, second, definition, substitutions);
        bool MatchList(IReadOnlyList<TypeSymbol> first, IReadOnlyList<TypeSymbol> second) => first.Count == second.Count
            && first.Zip(second).All(pair => Match(pair.First, pair.Second));
        if (target.Kind != current.Kind
            || target.Element is { } element && !Match(element, current.Element!)
            || !MatchList(target.Arguments, current.Arguments)
            || target.Modifier is { } modifier && !Match(modifier, current.Modifier!)
            || target.Signature is { } signature && (!Match(signature.ReturnType, current.Signature!.ReturnType)
                || !MatchList(signature.Parameters, current.Signature.Parameters)))
        {
            return false;
        }

        var substituted = SymbolRelations.Rewrite(target, type => type.Kind == TypeSymbolKind.MethodParameter && type.Owner == definition
            ? substitutions.GetValueOrDefault(type.Position) : null);
        return SymbolIdentity.Equal(substituted, current);
    }

    private static bool IsArrayElement(TypeSymbol type) => type.Kind switch
    {
        TypeSymbolKind.Array or TypeSymbolKind.SzArray or TypeSymbolKind.Modified => IsArrayElement(type.Element!),
        TypeSymbolKind.ByRef or TypeSymbolKind.Pinned => false,
        TypeSymbolKind.Pointer => IsPointerTarget(type.Element!),
        _ => !type.IsByRefLike && type.Keyword is not ("void" or "typedref"),
    };

    private static bool IsInstructionType(TypeSymbol type, string opcode)
    {
        while (type.Kind == TypeSymbolKind.Modified)
        {
            type = type.Element!;
        }

        return opcode switch
        {
            "newarr" or "ldelem" or "ldelema" or "stelem" => IsArrayElement(type),
            "box" or "unbox.any" or "castclass" or "isinst" => IsBoxable(type),
            "unbox" => IsBoxable(type) && (type.IsValueTypeShape || type.IsGenericParameter),
            "constrained." => IsStorageType(type) && type.Kind is not (TypeSymbolKind.Pointer or TypeSymbolKind.FunctionPointer),
            "ldobj" or "stobj" or "cpobj" or "initobj" or "sizeof" or "mkrefany" or "refanyval" => IsStorageType(type),
            _ => true,
        };
    }

    private static bool IsBoxable(TypeSymbol type) => IsStorageType(type) && !type.IsByRefLike
        && type.Keyword != "typedref" && type.Kind is not (TypeSymbolKind.Pointer or TypeSymbolKind.FunctionPointer);

    private static bool IsStorageType(TypeSymbol type) => type.Kind switch
    {
        TypeSymbolKind.Modified => IsStorageType(type.Element!),
        TypeSymbolKind.ByRef or TypeSymbolKind.Pinned => false,
        TypeSymbolKind.Array or TypeSymbolKind.SzArray => IsArrayElement(type.Element!),
        TypeSymbolKind.Pointer => IsPointerTarget(type.Element!),
        _ => !SymbolIdentity.Equal(type, TypeSymbol.Void),
    };

    private static bool IsVariableType(TypeSymbol type) => type.Kind switch
    {
        TypeSymbolKind.Modified or TypeSymbolKind.ByRef or TypeSymbolKind.Pinned => IsVariableType(type.Element!),
        TypeSymbolKind.Array or TypeSymbolKind.SzArray => IsArrayElement(type.Element!),
        TypeSymbolKind.Pointer => IsPointerTarget(type.Element!),
        _ => !SymbolIdentity.Equal(type, TypeSymbol.Void),
    };

    private static bool IsPointerTarget(TypeSymbol type) => type.Kind switch
    {
        TypeSymbolKind.Array or TypeSymbolKind.SzArray => IsArrayElement(type),
        TypeSymbolKind.Pointer or TypeSymbolKind.Modified => IsPointerTarget(type.Element!),
        TypeSymbolKind.ByRef or TypeSymbolKind.Pinned => false,
        _ => true,
    };

    private static bool ContainsParameter(TypeSymbol type)
    {
        var found = false;
        SymbolRelations.Rewrite(type, part =>
        {
            found |= part.IsGenericParameter;
            return null;
        });
        return found;
    }

    private static bool HasBase(TypeSymbol type, string path, IBindingScope scope)
    {
        var visited = new HashSet<TypeSymbol>();
        for (var current = type; current is not null && visited.Add(current); current = scope.BaseOf(current))
        {
            if (IsCoreType(current, path, scope))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCoreType(TypeSymbol type, string path, IBindingScope scope) =>
        SymbolIdentity.Equal(type, scope.LookupType(path, "System.Private.CoreLib", 0, false).Type);

    private static bool HasConstrainedInterfaceReceiver(MethodSymbol method, EditingView view)
    {
        if (method.DeclaringType is not { IsInterface: true } declaring
            || view.PrecedingInstruction is not { Op: var op, Operand.Type: { } receiver } || op != OpCodes.Constrained)
        {
            return false;
        }

        return SymbolRelations.AllInterfacesOf(receiver, view.Scope).Any(type => SymbolIdentity.Equal(type, declaring))
            || SymbolIdentity.Equal(receiver, declaring);
    }

    private static bool IsOverrideTarget(MethodSymbol method, EditingView view)
    {
        if (!method.IsVirtual || view.Owner is null || method.DeclaringType is null)
        {
            return false;
        }

        var allowedOwner = !SymbolIdentity.Equal(view.Owner.DefinitionOrSelf, method.DeclaringType.DefinitionOrSelf)
            && SymbolRelations.IsSameOrSubclassDefinition(view.Owner, method.DeclaringType, view.Scope.BaseOf)
            || SymbolRelations.AllInterfacesOf(view.Owner, view.Scope).Any(type => SymbolIdentity.Equal(type, method.DeclaringType));
        return allowedOwner && (view.OpenMethod is null
            || SignatureSymbolIdentity.Equal(method, view.OpenMethod) && method.IsStatic == view.OpenMethod.IsStatic);
    }
}
