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
/// <param name="Types">The types defined with <c>.class</c>, resolvable by name before any assembly is searched.</param>
public sealed record ParseContext(
    IReadOnlyList<LocalDeclaration> Locals,
    IReadOnlyList<ArgumentDeclaration> Arguments,
    GenericContext Generics,
    TypeResolver Resolver,
    IReadOnlyList<MethodSignature> Methods,
    TypeTable Types)
{
    /// <summary>
    /// Initializes a context with no session types.
    /// </summary>
    /// <param name="locals">The declared locals.</param>
    /// <param name="arguments">The declared arguments.</param>
    /// <param name="generics">The generic parameters in scope.</param>
    /// <param name="resolver">The type resolver.</param>
    /// <param name="methods">The session methods.</param>
    public ParseContext(IReadOnlyList<LocalDeclaration> locals, IReadOnlyList<ArgumentDeclaration> arguments, GenericContext generics, TypeResolver resolver, IReadOnlyList<MethodSignature> methods)
        : this(locals, arguments, generics, resolver, methods, TypeTable.Empty)
    {
    }

    /// <summary>
    /// The argument index that holds <c>this</c> inside an instance member, or -1.
    /// </summary>
    public int ThisIndex { get; init; } = -1;

    /// <summary>
    /// True when a lookup only inspects: nothing it names may be declared ahead of its declaration,
    /// so a reference to a member or nested type that does not exist yet is an error, not a promise.
    /// </summary>
    public bool Inspecting { get; init; }

    /// <summary>
    /// The scope accesses in this context are judged from, for the suggestions a failed lookup
    /// makes: a body's owner, or null for the cell. Null never means inspection.
    /// </summary>
    public AccessScope? Scope { get; init; }

    /// <summary>
    /// Returns a copy with different generic parameters in scope.
    /// </summary>
    /// <param name="generics">The generic context.</param>
    /// <returns>The new context.</returns>
    public ParseContext WithGenerics(GenericContext generics) => this with { Generics = generics };
}
