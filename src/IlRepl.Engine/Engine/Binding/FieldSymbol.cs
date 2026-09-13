using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// A field: its definition, the type it is referenced on, and its type as that reference sees it.
/// </summary>
public sealed class FieldSymbol : IEquatable<FieldSymbol>
{
    /// <summary>
    /// The definition's identity.
    /// </summary>
    public required DefinitionId Definition { get; init; }

    /// <summary>
    /// Where the field comes from: a loaded assembly, or a declaration of a type being written.
    /// </summary>
    public required MethodSymbolSource Source { get; init; }

    /// <summary>
    /// The type the field is referenced on, constructed when the reference names an instantiation.
    /// </summary>
    public required TypeSymbol DeclaringType { get; init; }

    /// <summary>
    /// The field name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The field type, with the declaring construction's arguments substituted.
    /// </summary>
    public required TypeSymbol FieldType { get; init; }

    /// <summary>
    /// The complete field type when annotations cannot be represented by <see cref="FieldType"/>.
    /// </summary>
    internal TypeSymbol? ExactType { get; init; }

    /// <summary>
    /// The field attributes.
    /// </summary>
    public FieldAttributes Attributes { get; init; }

    /// <summary>
    /// The <c>modreq</c> types on the field type.
    /// </summary>
    public IReadOnlyList<TypeSymbol> RequiredModifiers { get; init; } = [];

    /// <summary>
    /// The <c>modopt</c> types on the field type.
    /// </summary>
    public IReadOnlyList<TypeSymbol> OptionalModifiers { get; init; } = [];

    /// <summary>
    /// True for a static field.
    /// </summary>
    public bool IsStatic => Attributes.HasFlag(FieldAttributes.Static);

    /// <summary>
    /// True for a literal.
    /// </summary>
    public bool IsLiteral => Attributes.HasFlag(FieldAttributes.Literal);

    /// <summary>
    /// True for an initonly field.
    /// </summary>
    public bool IsInitOnly => Attributes.HasFlag(FieldAttributes.InitOnly);

    /// <summary>
    /// True for a public field.
    /// </summary>
    public bool IsPublic => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;

    /// <summary>
    /// A copy seen through a declaring construction.
    /// </summary>
    /// <param name="declaringType">The declaring construction.</param>
    /// <param name="fieldType">The substituted field type.</param>
    /// <returns>The copy.</returns>
    public FieldSymbol With(TypeSymbol declaringType, TypeSymbol fieldType)
        => WithExact(declaringType, fieldType, RuntimeSymbolTypes.RebaseExact(FieldType, ExactType, fieldType));

    /// <summary>
    /// A copy seen through a declaring construction with its complete substituted type.
    /// </summary>
    internal FieldSymbol WithExact(TypeSymbol declaringType, TypeSymbol fieldType, TypeSymbol? exactType)
    {
        ArgumentNullException.ThrowIfNull(declaringType);
        ArgumentNullException.ThrowIfNull(fieldType);
        return new FieldSymbol
        {
            Definition = Definition,
            Source = Source,
            DeclaringType = declaringType,
            Name = Name,
            FieldType = fieldType,
            ExactType = exactType,
            Attributes = Attributes,
            RequiredModifiers = RequiredModifiers,
            OptionalModifiers = OptionalModifiers,
        };
    }

    /// <inheritdoc/>
    public bool Equals(FieldSymbol? other) => SymbolIdentity.Equal(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FieldSymbol other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => SymbolIdentity.Hash(this);

    /// <inheritdoc/>
    public override string ToString() => (ExactType is null ? SymbolRenderer.Pretty(FieldType) : SymbolRenderer.Annotated(ExactType))
        + " " + SymbolRenderer.Pretty(DeclaringType) + "::" + Name;
}
