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
            ?? arguments.Select(argument => new ParameterSymbol(argument.Type, argument.Name)).ToArray();
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
        if (!Match(target.ReturnType, source?.ReturnType ?? TypeSymbol.Object)
            || !MatchList(target.ReturnRequiredModifiers, source?.ReturnRequiredModifiers ?? [])
            || !MatchList(target.ReturnOptionalModifiers, source?.ReturnOptionalModifiers ?? [])
            || !target.Parameters.Zip(parameters).All(pair => Match(pair.First.Type, pair.Second.Type)
                && MatchList(pair.First.RequiredModifiers, pair.Second.RequiredModifiers)
                && MatchList(pair.First.OptionalModifiers, pair.Second.OptionalModifiers)))
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
