namespace IlRepl.Engine.Binding;

/// <summary>
/// Holds the declaration copies and metadata caches shared by scopes within one snapshot.
/// </summary>
internal sealed class SnapshotBindingState
{
    /// <summary>
    /// Initializes the binding state with independently copied declarations.
    /// </summary>
    /// <param name="declarations">The declarations owned by this snapshot.</param>
    public SnapshotBindingState(Dictionary<DefinitionId, DeclarationSymbol> declarations)
    {
        Declarations = declarations;
    }

    /// <summary>
    /// The snapshot's declarations and accepted symbolic forward references.
    /// </summary>
    public Dictionary<DefinitionId, DeclarationSymbol> Declarations { get; }

    /// <summary>
    /// Method signatures decoded for each declaring construction.
    /// </summary>
    public Dictionary<TypeSymbol, IReadOnlyList<MethodSymbol>> Methods { get; } = [];

    /// <summary>
    /// Field signatures decoded for each declaring construction.
    /// </summary>
    public Dictionary<TypeSymbol, IReadOnlyList<FieldSymbol>> Fields { get; } = [];

    /// <summary>
    /// Generic constraints decoded for each definition.
    /// </summary>
    public Dictionary<DefinitionId, IReadOnlyList<GenericParameterSymbol>> Parameters { get; } = [];

    /// <summary>
    /// The lazily built type index used for spelling suggestions.
    /// </summary>
    public TypeIndex? Index { get; set; }
}
