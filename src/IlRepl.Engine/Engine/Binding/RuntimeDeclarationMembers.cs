using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// The declared members of a type being written, read from its <see cref="OwnMembers"/> and
/// described as symbols. Declaring a member ahead of its line goes through the block's own
/// callback, as it does when the resolver is asked directly.
/// </summary>
internal sealed class RuntimeDeclarationMembers : IDeclarationMembers
{
    private readonly RuntimeBindingScope _scope;
    private readonly OwnMembers _own;

    public RuntimeDeclarationMembers(RuntimeBindingScope scope, OwnMembers own, TypeSymbol declaring)
    {
        _scope = scope;
        _own = own;
        Declaring = declaring;
    }

    public TypeSymbol Declaring { get; }

    public TypeSymbol? BaseType => _own.BaseType is null ? null : _scope.ImportType(_own.BaseType);

    public IReadOnlyList<TypeSymbol> Interfaces => [.. _own.Interfaces.Select(_scope.ImportType)];

    public IReadOnlyList<FieldSymbol> Fields => [.. _own.Fields.Select(Import)];

    public FieldSymbol? FindField(string name) => _own.FindField(name) is { } found ? Import(found) : null;

    public IReadOnlyList<MethodSymbol> Methods => [.. _own.Methods.Select(Import)];

    public IEnumerable<MethodSymbol> FindMethods(string name) => _own.FindMethods(name).Select(Import);

    public bool CanDefineForward => _own.DefineForward is not null;

    public MethodSymbol DefineForward(MethodSymbol signature)
    {
        var declared = new MethodSignature(signature.Name, _scope.TypeOf(signature.ReturnType), [.. signature.Parameters.Select(p => new ArgumentDeclaration(_scope.TypeOf(p.Type), null, null, ""))])
        {
            Attributes = signature.Attributes,
            CallingConvention = signature.CallingConvention,
        };
        var builder = (_own.DefineForward ?? throw new InvalidOperationException("the type cannot take forward references"))(declared);
        return _scope.Register(RuntimeSymbolImporter.Import(declared, Declaring, RuntimeDefinitions.Of(builder), MethodSymbolSource.Forward, false), new RuntimeBindingScope.DeclaredMember(declared, builder));
    }

    private FieldSymbol Import((FieldDeclaration Declaration, FieldInfo Builder) field) =>
        _scope.Register(RuntimeSymbolImporter.Import(field.Declaration, Declaring, RuntimeDefinitions.Of(field.Builder)), new RuntimeBindingScope.DeclaredField(field.Declaration, field.Builder));

    private MethodSymbol Import((MethodSignature Signature, MethodBase Builder, bool Declared) method) =>
        _scope.Register(
            RuntimeSymbolImporter.Import(method.Signature, Declaring, RuntimeDefinitions.Of(method.Builder), method.Declared ? MethodSymbolSource.Declared : MethodSymbolSource.Forward, method.Declared),
            new RuntimeBindingScope.DeclaredMember(method.Signature, method.Builder));
}
