using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Compares a <c>jmp</c> target with the complete signature of its enclosing method.
/// </summary>
internal static class JumpCompatibility
{
    /// <summary>
    /// Explains an incompatible target, or returns null when the signatures match.
    /// </summary>
    public static string? Problem(MethodSymbol target, MethodSymbol? source, IReadOnlyList<VariableSymbol> arguments,
        IReadOnlyList<TypeSymbol> methodArguments, bool isVarArg, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(methodArguments);
        ArgumentNullException.ThrowIfNull(scope);
        if (source?.ImplAttributes.HasFlag(MethodImplAttributes.Synchronized) == true)
        {
            return "jmp is not allowed in a synchronized method";
        }

        if (target.IsAbstract)
        {
            return "jmp cannot target an abstract method";
        }

        var convention = source?.CallingConvention ?? (isVarArg ? CallingConventions.VarArgs : CallingConventions.Standard);
        var parameters = source?.Parameters
            ?? arguments.Select(CellParameter).ToArray();
        var isStatic = source?.IsStatic ?? true;
        var arity = source?.Arity ?? methodArguments.Count;
        if (target.IsStatic != isStatic || target.CallingConvention != convention
            || target.Arity != arity || target.Parameters.Count != parameters.Count)
        {
            return Incompatible;
        }

        var substitutions = target.IsGenericDefinition ? new Dictionary<int, TypeSymbol>() : null;
        bool Match(TypeSymbol expected, TypeSymbol current) => substitutions is null
            ? SymbolIdentity.Equal(expected, current)
            : MatchType(expected, current, target.Definition, substitutions);
        bool MatchList(IReadOnlyList<TypeSymbol> expected, IReadOnlyList<TypeSymbol> current) => expected.Count == current.Count
            && expected.Zip(current).All(pair => Match(pair.First, pair.Second));
        bool MatchAnnotated(
            TypeSymbol expected,
            TypeSymbol? exactExpected,
            IReadOnlyList<TypeSymbol> requiredExpected,
            IReadOnlyList<TypeSymbol> optionalExpected,
            TypeSymbol current,
            TypeSymbol? exactCurrent,
            IReadOnlyList<TypeSymbol> requiredCurrent,
            IReadOnlyList<TypeSymbol> optionalCurrent) => exactExpected is not null && exactCurrent is not null
                ? Match(exactExpected, exactCurrent)
                : Match(expected, current) && MatchList(requiredExpected, requiredCurrent)
                    && MatchList(optionalExpected, optionalCurrent);
        var returnMatches = source is null
            ? MatchAnnotated(target.ReturnType, target.ExactReturnType,
                target.ReturnRequiredModifiers, target.ReturnOptionalModifiers, TypeSymbol.Object, TypeSymbol.Object, [], [])
            : MatchAnnotated(
                target.ReturnType,
                target.ExactReturnType,
                target.ReturnRequiredModifiers,
                target.ReturnOptionalModifiers,
                source.ReturnType,
                source.ExactReturnType,
                source.ReturnRequiredModifiers,
                source.ReturnOptionalModifiers);
        if (!returnMatches || !target.Parameters.Zip(parameters).All(pair => MatchAnnotated(
            pair.First.Type,
            pair.First.ExactType,
            pair.First.RequiredModifiers,
            pair.First.OptionalModifiers,
            pair.Second.Type,
            pair.Second.ExactType,
            pair.Second.RequiredModifiers,
            pair.Second.OptionalModifiers)))
        {
            return Incompatible;
        }

        if (!target.IsStatic)
        {
            var receiver = arguments.Count == 0 ? null : arguments[0].Type;
            var owner = target.DeclaringType!;
            if (receiver is null || (owner.IsValueTypeShape
                ? !SymbolIdentity.Equal(receiver, TypeSymbol.ByRef(owner))
                : !SymbolRelations.IsAssignable(receiver, owner, scope)))
            {
                return Incompatible;
            }
        }

        return null;
    }

    private const string Incompatible =
        "jmp target must match the current method's calling convention, generic arity, parameters, and return type";

    private static ParameterSymbol CellParameter(VariableSymbol argument)
    {
        var exact = argument.ExactType ?? (RuntimeSymbolTypes.RequiresExact(argument.Type) ? argument.Type : null);
        var type = SymbolSignatureProvider.StripModifiers(exact ?? argument.Type, out var required, out var optional);
        return new ParameterSymbol(type, argument.Name)
        {
            ExactType = exact,
            RequiredModifiers = required,
            OptionalModifiers = optional,
        };
    }

    private static bool MatchType(TypeSymbol target, TypeSymbol current, DefinitionId definition,
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

        bool Match(TypeSymbol first, TypeSymbol second) => MatchType(first, second, definition, substitutions);
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

        var substituted = SymbolRelations.Rewrite(target, type => type.Kind == TypeSymbolKind.MethodParameter
            && type.Owner == definition ? substitutions.GetValueOrDefault(type.Position) : null);
        return SymbolIdentity.Equal(substituted, current);
    }
}
