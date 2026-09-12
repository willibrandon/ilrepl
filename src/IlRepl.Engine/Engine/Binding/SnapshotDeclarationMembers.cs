namespace IlRepl.Engine.Binding;

/// <summary>
/// Copies declaration members into a scope that can record independent symbolic forward references.
/// </summary>
internal sealed class SnapshotDeclarationMembers : IDeclarationMembers
{
    private static long s_forwards;
    private readonly DeclarationSymbol _declaration;
    private readonly bool _allowForward;

    /// <summary>
    /// Initializes the member view.
    /// </summary>
    /// <param name="declaration">The independently owned declaration snapshot.</param>
    /// <param name="allowForward">Whether a lookup may add a forward member.</param>
    public SnapshotDeclarationMembers(DeclarationSymbol declaration, bool allowForward = true)
    {
        _declaration = declaration;
        _allowForward = allowForward;
    }

    /// <inheritdoc/>
    public TypeSymbol Declaring => _declaration.Type;

    /// <inheritdoc/>
    public TypeSymbol? BaseType => _declaration.BaseType;

    /// <inheritdoc/>
    public IReadOnlyList<TypeSymbol> Interfaces => _declaration.Interfaces;

    /// <inheritdoc/>
    public IReadOnlyList<FieldSymbol> Fields => _declaration.Fields;

    /// <inheritdoc/>
    public FieldSymbol? FindField(string name) => _declaration.Fields.FirstOrDefault(f => f.Name == name);

    /// <inheritdoc/>
    public IReadOnlyList<MethodSymbol> Methods => _declaration.Methods;

    /// <inheritdoc/>
    public IEnumerable<MethodSymbol> FindMethods(string name) => _declaration.Methods.Where(m => m.Name == name);

    /// <inheritdoc/>
    public bool CanDefineForward => _allowForward && _declaration.CanDefineForward;

    /// <inheritdoc/>
    public MethodSymbol DefineForward(MethodSymbol signature)
    {
        if (!CanDefineForward)
        {
            throw new InvalidOperationException("the type cannot take forward references");
        }

        var defined = new MethodSymbol
        {
            Definition = DefinitionId.ForDeclaration(_declaration.Type.Definition.Assembly, -Interlocked.Increment(ref s_forwards)),
            Source = MethodSymbolSource.Forward,
            DeclaringType = _declaration.Type,
            Name = signature.Name,
            Attributes = signature.Attributes,
            CallingConvention = signature.CallingConvention,
            ReturnType = signature.ReturnType,
            ExactReturnType = signature.ExactReturnType,
            Parameters = signature.Parameters,
            GenericParameters = signature.GenericParameters,
            GenericArguments = signature.GenericArguments,
            ReturnRequiredModifiers = signature.ReturnRequiredModifiers,
            ReturnOptionalModifiers = signature.ReturnOptionalModifiers,
            IsDeclared = false,
        };
        _declaration.AddForward(defined);
        return defined;
    }
}
