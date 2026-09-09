namespace IlRepl.Engine.Binding;

/// <summary>
/// Supplies symbolic stack operations with markers corresponding to the runtime stack model.
/// </summary>
/// <remarks>
/// The stack type algebra over symbols. Its markers are the symbols of the same marker types the
/// runtime model uses, so a preview's stack and an actual stack name the same things.
/// </remarks>
public sealed class SymbolStackAlgebra : IStackTypeAlgebra<TypeSymbol>
{
    private static readonly TypeSymbol BoxedDefinition = RuntimeSymbolImporter.Import(typeof(Boxed<>));
    private static readonly TypeSymbol NullableDefinition = RuntimeSymbolImporter.Import(typeof(Nullable<>));
    private static readonly Dictionary<string, TypeSymbol> CoreLibTypes = new(StringComparer.Ordinal)
    {
        ["System.RuntimeTypeHandle"] = RuntimeSymbolImporter.Import(typeof(RuntimeTypeHandle)),
        ["System.RuntimeFieldHandle"] = RuntimeSymbolImporter.Import(typeof(RuntimeFieldHandle)),
        ["System.RuntimeMethodHandle"] = RuntimeSymbolImporter.Import(typeof(RuntimeMethodHandle)),
        ["System.RuntimeArgumentHandle"] = RuntimeSymbolImporter.Import(typeof(RuntimeArgumentHandle)),
    };

    /// <summary>
    /// The one instance.
    /// </summary>
    public static SymbolStackAlgebra Instance { get; } = new();

    /// <summary>
    /// The transfer over this algebra.
    /// </summary>
    public static StackTransfer<TypeSymbol> Transfer { get; } = new(Instance);

    /// <inheritdoc/>
    public TypeSymbol Primitive(string keyword) => TypeSymbol.Primitive(keyword);

    /// <inheritdoc/>
    public TypeSymbol CoreLib(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        return CoreLibTypes.TryGetValue(fullName, out var symbol) ? symbol : throw new ArgumentException(
            $"'{fullName}' is not a type the stack model names", nameof(fullName));
    }

    /// <inheritdoc/>
    public TypeSymbol NullReference { get; } = RuntimeSymbolImporter.Import(typeof(NullReferenceMarker));

    /// <inheritdoc/>
    public TypeSymbol UnknownReference { get; } = RuntimeSymbolImporter.Import(typeof(UnknownReferenceMarker));

    /// <inheritdoc/>
    public TypeSymbol Boxed(TypeSymbol valueType) => TypeSymbol.Construct(BoxedDefinition, [valueType]);

    /// <summary>
    /// The value type behind a boxed entry, or null when the entry is not a boxed value.
    /// </summary>
    /// <param name="type">The stack entry.</param>
    /// <returns>The boxed value type, or null.</returns>
    public static TypeSymbol? BoxedType(TypeSymbol? type) =>
        type is { Kind: TypeSymbolKind.Constructed } && SymbolIdentity.Equal(type.Element, BoxedDefinition) ? type.Arguments[0] : null;

    /// <inheritdoc/>
    public TypeSymbol? NullableUnderlying(TypeSymbol type) =>
        type is { Kind: TypeSymbolKind.Constructed } && SymbolIdentity.Equal(type.Element, NullableDefinition) ? type.Arguments[0] : null;

    /// <inheritdoc/>
    public TypeSymbol MakeByRef(TypeSymbol type) => TypeSymbol.ByRef(type);

    /// <inheritdoc/>
    public TypeSymbol MakeArray(TypeSymbol type) => TypeSymbol.SzArray(type);

    /// <inheritdoc/>
    public TypeSymbol? ElementOf(TypeSymbol type)
        => type.Kind is TypeSymbolKind.ByRef or TypeSymbolKind.Pointer or TypeSymbolKind.SzArray or TypeSymbolKind.Array ? type.Element
            : null;

    /// <inheritdoc/>
    public bool IsByRef(TypeSymbol type) => type.Kind == TypeSymbolKind.ByRef;

    /// <inheritdoc/>
    public bool IsPointer(TypeSymbol type) => type.Kind == TypeSymbolKind.Pointer;

    /// <inheritdoc/>
    public bool IsArray(TypeSymbol type) => type.IsArray;

    /// <inheritdoc/>
    public bool IsValueType(TypeSymbol type) => type.IsValueTypeShape;

    /// <inheritdoc/>
    public bool IsGenericParameter(TypeSymbol type) => type.IsGenericParameter;

    /// <inheritdoc/>
    public bool Same(TypeSymbol? a, TypeSymbol? b) => SymbolIdentity.Equal(a, b);
}
