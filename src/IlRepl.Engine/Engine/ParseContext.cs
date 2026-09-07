namespace IlRepl.Engine;

/// <summary>
/// Everything a line needs to be parsed: the locals and arguments it can name, the generic
/// parameters in scope, the type resolver, and the session methods a call can name without a type.
/// </summary>
/// <param name="Locals">The declared locals, by index.</param>
/// <param name="Arguments">The declared arguments or parameters, by index.</param>
/// <param name="Generics">The generic parameters in scope for <c>!N</c> and <c>!!N</c>.</param>
/// <param name="Resolver">The type resolver.</param>
/// <param name="Methods">The methods defined with <c>.method</c>, resolvable by bare name.</param>
public sealed record ParseContext(
    IReadOnlyList<LocalDeclaration> Locals,
    IReadOnlyList<ArgumentDeclaration> Arguments,
    GenericContext Generics,
    TypeResolver Resolver,
    IReadOnlyList<MethodSignature> Methods)
{
    /// <summary>
    /// Returns a copy with different generic parameters in scope.
    /// </summary>
    /// <param name="generics">The generic context.</param>
    /// <returns>The new context.</returns>
    public ParseContext WithGenerics(GenericContext generics) => this with { Generics = generics };
}
