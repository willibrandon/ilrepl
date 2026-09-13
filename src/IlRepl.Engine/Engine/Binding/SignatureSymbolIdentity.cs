namespace IlRepl.Engine.Binding;

/// <summary>
/// Compares method signatures with their own generic parameters matched by position.
/// </summary>
internal static class SignatureSymbolIdentity
{
    /// <summary>
    /// Compares return and parameter shapes independently of each method's generic owner identity.
    /// </summary>
    /// <param name="first">The first signature.</param>
    /// <param name="second">The second signature.</param>
    /// <returns>Whether their CLI method signatures agree.</returns>
    public static bool Equal(MethodSymbol first, MethodSymbol second) => first.IsStatic == second.IsStatic
        && first.Arity == second.Arity && EqualReturn(first, second)
        && first.Parameters.Count == second.Parameters.Count
        && first.Parameters.Zip(second.Parameters).All(pair => Equal(pair.First, pair.Second));

    /// <summary>
    /// Compares complete return types, or their separate modifier sets when joint order is unavailable.
    /// </summary>
    internal static bool EqualReturn(MethodSymbol first, MethodSymbol second) => EqualAnnotated(
        first.ReturnType,
        first.ExactReturnType,
        first.ReturnRequiredModifiers,
        first.ReturnOptionalModifiers,
        second.ReturnType,
        second.ExactReturnType,
        second.ReturnRequiredModifiers,
        second.ReturnOptionalModifiers);

    /// <summary>
    /// Compares complete parameter types, or their separate modifier sets when joint order is unavailable.
    /// </summary>
    internal static bool Equal(ParameterSymbol first, ParameterSymbol second) => EqualAnnotated(
        first.Type,
        first.ExactType,
        first.RequiredModifiers,
        first.OptionalModifiers,
        second.Type,
        second.ExactType,
        second.RequiredModifiers,
        second.OptionalModifiers);

    /// <summary>
    /// Compares a parameter with the complete type written in a member reference.
    /// </summary>
    internal static bool Equal(ParameterSymbol parameter, BoundType reference) => EqualAnnotated(
        parameter.Type,
        parameter.ExactType,
        parameter.RequiredModifiers,
        parameter.OptionalModifiers,
        reference.Type,
        reference.ExactType,
        reference.RequiredModifiers,
        reference.OptionalModifiers);

    /// <summary>
    /// Compares a method return with the complete type written in a member reference.
    /// </summary>
    internal static bool EqualReturn(MethodSymbol method, BoundType reference) => EqualAnnotated(
        method.ReturnType,
        method.ExactReturnType,
        method.ReturnRequiredModifiers,
        method.ReturnOptionalModifiers,
        reference.Type,
        reference.ExactType,
        reference.RequiredModifiers,
        reference.OptionalModifiers);

    /// <summary>
    /// Compares complete field types, or their separate modifier sets when joint order is unavailable.
    /// </summary>
    internal static bool Equal(FieldSymbol first, FieldSymbol second) => EqualAnnotated(
        first.FieldType,
        first.ExactType,
        first.RequiredModifiers,
        first.OptionalModifiers,
        second.FieldType,
        second.ExactType,
        second.RequiredModifiers,
        second.OptionalModifiers);

    /// <summary>
    /// Compares a field with the complete type written in a field reference.
    /// </summary>
    internal static bool Equal(FieldSymbol field, BoundType reference) => EqualAnnotated(
        field.FieldType,
        field.ExactType,
        field.RequiredModifiers,
        field.OptionalModifiers,
        reference.Type,
        reference.ExactType,
        reference.RequiredModifiers,
        reference.OptionalModifiers);

    /// <summary>
    /// Returns the complete annotated return type of a method signature.
    /// </summary>
    internal static TypeSymbol AnnotatedReturn(MethodSymbol method) => method.ExactReturnType
        ?? Annotate(method.ReturnType, method.ReturnRequiredModifiers, method.ReturnOptionalModifiers);

    /// <summary>
    /// Returns the complete annotated type of a parameter.
    /// </summary>
    internal static TypeSymbol Annotated(ParameterSymbol parameter) => parameter.ExactType
        ?? Annotate(parameter.Type, parameter.RequiredModifiers, parameter.OptionalModifiers);

    /// <summary>
    /// Returns the complete annotated type of a field.
    /// </summary>
    internal static TypeSymbol Annotated(FieldSymbol field) => field.ExactType
        ?? Annotate(field.FieldType, field.RequiredModifiers, field.OptionalModifiers);

    private static TypeSymbol Annotate(
        TypeSymbol type,
        IReadOnlyList<TypeSymbol> required,
        IReadOnlyList<TypeSymbol> optional)
    {
        foreach (var modifier in optional)
        {
            type = TypeSymbol.Modified(type, modifier, false);
        }

        foreach (var modifier in required)
        {
            type = TypeSymbol.Modified(type, modifier, true);
        }

        return type;
    }

    private static bool EqualAnnotated(
        TypeSymbol first,
        TypeSymbol? firstExact,
        IReadOnlyList<TypeSymbol> firstRequired,
        IReadOnlyList<TypeSymbol> firstOptional,
        TypeSymbol second,
        TypeSymbol? secondExact,
        IReadOnlyList<TypeSymbol> secondRequired,
        IReadOnlyList<TypeSymbol> secondOptional)
    {
        if (firstExact is not null && secondExact is not null)
        {
            return Equal(firstExact, secondExact);
        }

        if (firstExact is not null)
        {
            first = SymbolSignatureProvider.StripModifiers(firstExact, out firstRequired, out firstOptional);
        }

        if (secondExact is not null)
        {
            second = SymbolSignatureProvider.StripModifiers(secondExact, out secondRequired, out secondOptional);
        }

        return Equal(first, second)
            && Equal(firstRequired, secondRequired)
            && Equal(firstOptional, secondOptional);
    }

    private static bool Equal(IReadOnlyList<TypeSymbol> first, IReadOnlyList<TypeSymbol> second) => first.Count == second.Count
        && first.Zip(second).All(pair => Equal(pair.First, pair.Second));

    private static bool Equal(TypeSymbol first, TypeSymbol second)
    {
        TypeSymbol Normalize(TypeSymbol type) => SymbolRelations.Rewrite(type,
            parameter => parameter.Kind == TypeSymbolKind.MethodParameter
                ? TypeSymbol.Parameter(DefinitionId.None, true, parameter.Position, parameter.Name, parameter.ParameterAttributes)
                : null);
        return SymbolIdentity.Equal(Normalize(first), Normalize(second));
    }
}
