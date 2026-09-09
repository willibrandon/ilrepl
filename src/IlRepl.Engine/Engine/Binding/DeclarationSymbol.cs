namespace IlRepl.Engine.Binding;

/// <summary>
/// A type the session is writing, or a placeholder for one, as a snapshot copies it: the facts of
/// its header and the members declared so far, held as symbols with no builder behind them. A
/// snapshot may add members a reference declares ahead of its line; the real block never sees them.
/// </summary>
public sealed class DeclarationSymbol
{
    private readonly List<FieldSymbol> _fields;
    private readonly List<MethodSymbol> _methods;

    /// <summary>
    /// Initializes a declaration from copied facts.
    /// </summary>
    /// <param name="type">The type symbol, carrying the declaration's identity.</param>
    /// <param name="baseType">The base type, or null for an interface.</param>
    /// <param name="interfaces">The interfaces declared.</param>
    /// <param name="genericParameters">The generic parameters declared.</param>
    /// <param name="fields">The fields declared so far.</param>
    /// <param name="methods">The methods declared so far, forward references included.</param>
    /// <param name="canDefineForward">True when the type takes references to members declared later.</param>
    public DeclarationSymbol(TypeSymbol type, TypeSymbol? baseType, IReadOnlyList<TypeSymbol> interfaces, IReadOnlyList<GenericParameterSymbol> genericParameters, IEnumerable<FieldSymbol> fields, IEnumerable<MethodSymbol> methods, bool canDefineForward)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(genericParameters);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(methods);
        Type = type;
        BaseType = baseType;
        Interfaces = interfaces;
        GenericParameters = genericParameters;
        _fields = [.. fields];
        _methods = [.. methods];
        CanDefineForward = canDefineForward;
    }

    /// <summary>
    /// The type, carrying the declaration's identity and header facts.
    /// </summary>
    public TypeSymbol Type { get; }

    /// <summary>
    /// The base type, or null for an interface.
    /// </summary>
    public TypeSymbol? BaseType { get; }

    /// <summary>
    /// The interfaces declared.
    /// </summary>
    public IReadOnlyList<TypeSymbol> Interfaces { get; }

    /// <summary>
    /// The generic parameters declared, with their constraints.
    /// </summary>
    public IReadOnlyList<GenericParameterSymbol> GenericParameters { get; }

    /// <summary>
    /// The fields declared so far.
    /// </summary>
    public IReadOnlyList<FieldSymbol> Fields => _fields;

    /// <summary>
    /// The methods declared so far, and the ones referenced before their declaration.
    /// </summary>
    public IReadOnlyList<MethodSymbol> Methods => _methods;

    /// <summary>
    /// True when the type takes references to members declared later.
    /// </summary>
    public bool CanDefineForward { get; }

    /// <summary>
    /// True for a placeholder standing in for a nested type declared later.
    /// </summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>
    /// Records a member referenced before its declaration in this copy alone.
    /// </summary>
    /// <param name="method">The member with its identity.</param>
    public void AddForward(MethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);
        _methods.Add(method);
    }

    /// <summary>
    /// An independent copy, so an edit to one preview never shows in another.
    /// </summary>
    /// <returns>The copy.</returns>
    public DeclarationSymbol Clone() => new(Type, BaseType, Interfaces, GenericParameters, _fields, _methods, CanDefineForward) { IsPlaceholder = IsPlaceholder };
}
