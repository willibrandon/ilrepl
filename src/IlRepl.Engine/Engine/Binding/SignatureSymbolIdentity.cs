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
        && first.Arity == second.Arity && Equal(first.ReturnType, second.ReturnType)
        && first.Parameters.Count == second.Parameters.Count
        && first.Parameters.Zip(second.Parameters).All(pair => Equal(pair.First.Type, pair.Second.Type));

    private static bool Equal(TypeSymbol first, TypeSymbol second)
    {
        TypeSymbol Normalize(TypeSymbol type) => SymbolRelations.Rewrite(type,
            parameter => parameter.Kind == TypeSymbolKind.MethodParameter
                ? TypeSymbol.Parameter(DefinitionId.None, true, parameter.Position, parameter.Name, parameter.ParameterAttributes)
                : null);
        return SymbolIdentity.Equal(Normalize(first), Normalize(second));
    }
}
