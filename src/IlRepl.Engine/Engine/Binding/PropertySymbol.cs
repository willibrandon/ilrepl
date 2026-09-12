namespace IlRepl.Engine.Binding;

/// <summary>
/// Identifies a property's metadata signature and the accessibility of its accessors.
/// </summary>
/// <param name="Definition">The property definition's identity.</param>
/// <param name="DeclaringType">The declaring construction.</param>
/// <param name="Name">The property name.</param>
/// <param name="Type">The property type.</param>
/// <param name="Parameters">The index parameter types.</param>
/// <param name="IsPublic">Whether at least one accessor is public.</param>
/// <param name="IsStatic">Whether the accessors are static.</param>
public sealed record PropertySymbol(
    DefinitionId Definition,
    TypeSymbol DeclaringType,
    string Name,
    TypeSymbol Type,
    IReadOnlyList<TypeSymbol> Parameters,
    bool IsPublic,
    bool IsStatic)
{
    /// <summary>
    /// The complete property type when annotations cannot be represented by <see cref="Type"/>.
    /// </summary>
    internal TypeSymbol? ExactType { get; init; }

    /// <summary>
    /// The complete index parameter types, with null where <see cref="Parameters"/> is exact.
    /// </summary>
    internal IReadOnlyList<TypeSymbol?> ExactParameters { get; init; } = [];
}
