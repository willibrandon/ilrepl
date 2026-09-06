namespace IlRepl.Engine;

/// <summary>
/// Everything a line needs to be parsed: declared locals and arguments, generic parameters, and the type resolver.
/// </summary>
/// <param name="Locals">The locals declared so far.</param>
/// <param name="Arguments">The cell arguments declared so far.</param>
/// <param name="Generics">The generic parameters that <c>!N</c> and <c>!!N</c> refer to.</param>
/// <param name="Resolver">The resolver used for type and member lookups.</param>
public sealed record ParseContext(
    IReadOnlyList<LocalDeclaration> Locals,
    IReadOnlyList<ArgumentDeclaration> Arguments,
    GenericContext Generics,
    TypeResolver Resolver)
{
    /// <summary>
    /// Returns a copy with different generic parameters in scope.
    /// </summary>
    /// <param name="generics">The generic context to use.</param>
    /// <returns>The new context.</returns>
    public ParseContext WithGenerics(GenericContext generics) => this with { Generics = generics };
}
