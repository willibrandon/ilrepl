using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Copies the members of an accepting runtime type into symbolic signatures.
/// </summary>
internal sealed class RuntimeDeclarationMembers : IDeclarationMembers
{
    private readonly RuntimeBindingScope _scope;
    private readonly OwnMembers _own;

    /// <summary>
    /// Initializes the member view.
    /// </summary>
    /// <param name="scope">The accepting runtime scope.</param>
    /// <param name="own">The declarations and builders of the open type.</param>
    /// <param name="declaring">The declaring type identity.</param>
    public RuntimeDeclarationMembers(RuntimeBindingScope scope, OwnMembers own, TypeSymbol declaring)
    {
        _scope = scope;
        _own = own;
        Declaring = declaring;
    }

    /// <inheritdoc/>
    public TypeSymbol Declaring { get; }

    /// <inheritdoc/>
    public TypeSymbol? BaseType => _own.BaseType is null ? null : _scope.ImportType(_own.BaseType);

    /// <inheritdoc/>
    public IReadOnlyList<TypeSymbol> Interfaces => [.. _own.Interfaces.Select(_scope.ImportType)];

    /// <inheritdoc/>
    public IReadOnlyList<FieldSymbol> Fields => [.. _own.Fields.Select(Import)];

    /// <inheritdoc/>
    public FieldSymbol? FindField(string name) => _own.FindField(name) is { } found ? Import(found) : null;

    /// <inheritdoc/>
    public IReadOnlyList<MethodSymbol> Methods => [.. _own.Methods.Select(Import)];

    /// <inheritdoc/>
    public IEnumerable<MethodSymbol> FindMethods(string name) => _own.FindMethods(name).Select(Import);

    /// <inheritdoc/>
    public bool CanDefineForward => _own.DefineForward is not null;

    /// <inheritdoc/>
    public MethodSymbol DefineForward(MethodSymbol signature)
    {
        var declared = new MethodSignature(signature.Name, _scope.TypeOf(signature.ReturnType), [.. signature.Parameters.Select(p
            => new ArgumentDeclaration(_scope.TypeOf(p.Type), null, null, "")
            {
                ExactType = RuntimeSymbolTypes.RequiresExact(p.Type) ? p.Type : null,
            })])
        {
            ExactSymbol = RuntimeSymbolTypes.RequiresExact(signature.ReturnType)
                || signature.Parameters.Any(parameter => RuntimeSymbolTypes.RequiresExact(parameter.Type)) ? signature : null,
            Attributes = signature.Attributes,
            CallingConvention = signature.CallingConvention,
        };
        var builder = (_own.DefineForward ?? throw new InvalidOperationException("the type cannot take forward references"))(declared);
        return _scope.Register(
            RuntimeSymbolImporter.Import(declared, Declaring, RuntimeDefinitions.Of(builder), MethodSymbolSource.Forward, false),
            new RuntimeDeclaredMember(declared, builder));
    }

    private FieldSymbol Import((FieldDeclaration Declaration, FieldInfo Builder) field) =>
        _scope.Register(RuntimeSymbolImporter.Import(field.Declaration, Declaring, RuntimeDefinitions.Of(field.Builder)),
            new RuntimeDeclaredField(field.Declaration, field.Builder));

    private MethodSymbol Import((MethodSignature Signature, MethodBase Builder, bool Declared) method) =>
        _scope.Register(
            RuntimeSymbolImporter.Import(method.Signature, Declaring, RuntimeDefinitions.Of(method.Builder),
                method.Declared ? MethodSymbolSource.Declared : MethodSymbolSource.Forward, method.Declared),
            new RuntimeDeclaredMember(method.Signature, method.Builder));
}
