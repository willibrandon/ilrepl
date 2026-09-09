namespace IlRepl.Engine.Binding;

/// <summary>
/// The members of a type being written, read from a snapshot's copy of its declaration. A
/// member declared ahead of its line is recorded in the copy, with an identity of its own, and
/// the real block never hears of it.
/// </summary>
internal sealed class SnapshotDeclarationMembers : IDeclarationMembers
{
    private static long s_forwards;
    private readonly DeclarationSymbol _declaration;

    public SnapshotDeclarationMembers(DeclarationSymbol declaration)
    {
        _declaration = declaration;
    }

    public TypeSymbol Declaring => _declaration.Type;

    public TypeSymbol? BaseType => _declaration.BaseType;

    public IReadOnlyList<TypeSymbol> Interfaces => _declaration.Interfaces;

    public IReadOnlyList<FieldSymbol> Fields => _declaration.Fields;

    public FieldSymbol? FindField(string name) => _declaration.Fields.FirstOrDefault(f => f.Name == name);

    public IReadOnlyList<MethodSymbol> Methods => _declaration.Methods;

    public IEnumerable<MethodSymbol> FindMethods(string name) => _declaration.Methods.Where(m => m.Name == name);

    public bool CanDefineForward => _declaration.CanDefineForward;

    public MethodSymbol DefineForward(MethodSymbol signature)
    {
        if (!_declaration.CanDefineForward)
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
            Parameters = signature.Parameters,
            IsDeclared = false,
        };
        _declaration.AddForward(defined);
        return defined;
    }
}
