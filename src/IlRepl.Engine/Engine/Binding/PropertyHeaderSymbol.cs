using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// The header of a <c>.property</c> block before its accessors are known.
/// </summary>
/// <param name="Name">The property name.</param>
/// <param name="Type">The property type.</param>
/// <param name="ParameterTypes">The index parameter types.</param>
/// <param name="IsStatic">True when the accessors are static.</param>
/// <param name="Attributes">The property attributes.</param>
/// <param name="OpensBlock">True when the header ended with <c>{</c>.</param>
public sealed record PropertyHeaderSymbol(
    string Name,
    TypeSymbol Type,
    IReadOnlyList<TypeSymbol> ParameterTypes,
    bool IsStatic,
    PropertyAttributes Attributes,
    bool OpensBlock)
{
    /// <summary>
    /// The complete property type when annotations cannot be represented by <see cref="Type"/>.
    /// </summary>
    internal TypeSymbol? ExactType { get; init; }

    /// <summary>
    /// The complete index parameter types, with null where <see cref="ParameterTypes"/> is exact.
    /// </summary>
    internal IReadOnlyList<TypeSymbol?> ExactParameterTypes { get; init; } = [];
}
