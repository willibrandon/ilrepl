namespace IlRepl.Engine.Binding;

/// <summary>
/// What a binder asks of its surroundings: how a written name finds a type, what is in generic
/// scope, which members a type has, and how a type or member is spelled in a message. The
/// runtime scope answers from the session's tables and reflection, and may load or declare
/// ahead; a snapshot scope answers from captured metadata and declarations and never does.
/// </summary>
public interface IBindingScope
{
    /// <summary>
    /// True when a lookup only inspects: nothing it names may be declared ahead of its declaration.
    /// </summary>
    bool Inspecting { get; }

    /// <summary>
    /// The generic parameters in scope for <c>!N</c> and <c>!!N</c>.
    /// </summary>
    SymbolGenericContext Generics { get; }

    /// <summary>
    /// A copy of the scope with different generic parameters in scope.
    /// </summary>
    /// <param name="generics">The generic context.</param>
    /// <returns>The new scope.</returns>
    IBindingScope WithGenerics(SymbolGenericContext generics);

    /// <summary>
    /// Finds the type a written name refers to: the session's types first, then the assemblies.
    /// </summary>
    /// <param name="name">The name as written, quoted segments decoded, with its arity suffix when written.</param>
    /// <param name="assemblyHint">The assembly named in square brackets, or null.</param>
    /// <param name="writtenArity">How many generic arguments follow the name; 0 when none.</param>
    /// <param name="valueTypeKeyword">True when the reference was written with <c>valuetype</c>, which decides the kind of a placeholder.</param>
    /// <returns>The definition and where it came from.</returns>
    /// <exception cref="ReplException">No type matched, or a short name was ambiguous.</exception>
    TypeLookupResult LookupType(string name, string? assemblyHint, int writtenArity, bool valueTypeKeyword);

    /// <summary>
    /// The type the <c>decimal</c> alias names.
    /// </summary>
    /// <returns>The symbol for <c>System.Decimal</c>.</returns>
    TypeSymbol LookupDecimal();

    /// <summary>
    /// The generic parameters of a definition, as types, or the arguments of a construction.
    /// </summary>
    /// <param name="type">The definition or construction.</param>
    /// <returns>The parameters or arguments in order; empty for a non-generic type.</returns>
    IReadOnlyList<TypeSymbol> GenericArgumentsOf(TypeSymbol type);

    /// <summary>
    /// The declared members of a type being written, when the declaring type is one.
    /// </summary>
    /// <param name="declaring">The declaring type, its definition, or a construction of it.</param>
    /// <param name="members">The members.</param>
    /// <returns>True when the type is being written.</returns>
    bool TryGetDeclaration(TypeSymbol declaring, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDeclarationMembers? members);

    /// <summary>
    /// True for a type the session declared, written or accepted.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>True for a session type.</returns>
    bool IsSessionType(TypeSymbol type);

    /// <summary>
    /// True when the declaring construction cannot list its own members and its definition must
    /// answer with substitution: a loaded generic type instantiated over a parameter of the cell
    /// or of a method being written.
    /// </summary>
    /// <param name="declaring">The declaring construction.</param>
    /// <returns>True when the definition must be consulted.</returns>
    bool RequiresDefinitionLookup(TypeSymbol declaring);

    /// <summary>
    /// The loaded methods with a name that a declaring type offers, inherited ones included, as
    /// reflection lists them: each on the type that declares it.
    /// </summary>
    /// <param name="declaring">The declaring type.</param>
    /// <param name="name">The method name.</param>
    /// <returns>The candidates.</returns>
    IReadOnlyList<MethodSymbol> Methods(TypeSymbol declaring, string name);

    /// <summary>
    /// The loaded constructors of a declaring type.
    /// </summary>
    /// <param name="declaring">The declaring type.</param>
    /// <param name="isStatic">True for the type initializer, false for instance constructors.</param>
    /// <returns>The constructors.</returns>
    IReadOnlyList<MethodSymbol> Constructors(TypeSymbol declaring, bool isStatic);

    /// <summary>
    /// A loaded field of a declaring type, inherited ones included.
    /// </summary>
    /// <param name="declaring">The declaring type.</param>
    /// <param name="name">The field name.</param>
    /// <returns>The field, or null.</returns>
    FieldSymbol? Field(TypeSymbol declaring, string name);

    /// <summary>
    /// The loaded fields of a declaring type, for a message.
    /// </summary>
    /// <param name="declaring">The declaring type.</param>
    /// <returns>The fields.</returns>
    IReadOnlyList<FieldSymbol> Fields(TypeSymbol declaring);

    /// <summary>
    /// Instantiates a generic method definition, or refuses when the arguments violate its constraints.
    /// </summary>
    /// <param name="definition">The generic method definition.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The instantiated method, or null when the constraints refuse it.</returns>
    MethodSymbol? Instantiate(MethodSymbol definition, IReadOnlyList<TypeSymbol> arguments);

    /// <summary>
    /// The base type, with the type's generic arguments substituted into it; null for object and interfaces.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The base type.</returns>
    TypeSymbol? BaseOf(TypeSymbol type);

    /// <summary>
    /// The interfaces the type declares, with its generic arguments substituted into them; for a
    /// generic parameter, its interface constraints.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The declared interfaces.</returns>
    IReadOnlyList<TypeSymbol> DeclaredInterfacesOf(TypeSymbol type);

    /// <summary>
    /// The generic parameters a definition declares, with their constraints.
    /// </summary>
    /// <param name="definition">A type definition, a construction of one, or a method.</param>
    /// <returns>The parameters, or empty.</returns>
    IReadOnlyList<GenericParameterSymbol> GenericParameterDeclarations(TypeSymbol definition);

    /// <summary>
    /// The declaration of a generic parameter, with its constraints, or null when its owner is unknown.
    /// </summary>
    /// <param name="parameter">A type or method generic parameter.</param>
    /// <returns>The declaration, or null.</returns>
    GenericParameterSymbol? ParameterDeclaration(TypeSymbol parameter);

    /// <summary>
    /// True when a loaded type is an enum, and its underlying type.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The underlying primitive of an enum, or null.</returns>
    TypeSymbol? EnumUnderlyingType(TypeSymbol type);

    /// <summary>
    /// Every loaded method a declaring type offers, whatever its name, as reflection lists them.
    /// </summary>
    /// <param name="declaring">The declaring type.</param>
    /// <returns>The methods.</returns>
    IReadOnlyList<MethodSymbol> AllMethods(TypeSymbol declaring);

    /// <summary>
    /// Where accesses in this scope are judged from.
    /// </summary>
    AccessContext Access { get; }

    /// <summary>
    /// A scope that answers from captured metadata alone, for the suggestions a failed lookup
    /// makes: this scope when it already is one, else a snapshot of it, which the lease releases.
    /// </summary>
    /// <param name="lease">What to dispose once the suggestion is made, or null.</param>
    /// <returns>The scope.</returns>
    IBindingScope ForSuggestions(out IDisposable? lease);

    /// <summary>
    /// The methods defined with <c>.method</c> at the top level, resolvable by bare name.
    /// </summary>
    IReadOnlyList<MethodSymbol> SessionMethods { get; }

    /// <summary>
    /// The declared locals, by index.
    /// </summary>
    IReadOnlyList<VariableSymbol> Locals { get; }

    /// <summary>
    /// The declared arguments or parameters, by index.
    /// </summary>
    IReadOnlyList<VariableSymbol> Arguments { get; }

    /// <summary>
    /// The argument index that holds <c>this</c> inside an instance member, or -1.
    /// </summary>
    int ThisIndex { get; }

    /// <summary>
    /// Spells a type the way a message names it.
    /// </summary>
    /// <param name="type">The type, or null for an unknown one.</param>
    /// <returns>The short name.</returns>
    string Pretty(TypeSymbol? type);

    /// <summary>
    /// Spells a loaded member the way a candidate list shows it.
    /// </summary>
    /// <param name="method">The member.</param>
    /// <returns>The IL-style signature.</returns>
    string Describe(MethodSymbol method);
}
