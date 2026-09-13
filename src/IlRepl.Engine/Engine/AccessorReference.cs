using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// An accessor line inside a property or event block: which accessor, and the method it names.
/// </summary>
/// <param name="Kind"><c>get</c>, <c>set</c>, <c>other</c>, <c>addon</c>, <c>removeon</c>, or <c>fire</c>.</param>
/// <param name="Name">The method name.</param>
/// <param name="ReturnType">The method's return type.</param>
/// <param name="ParameterTypes">The method's parameter types.</param>
/// <param name="IsStatic">True for a static accessor.</param>
public sealed record AccessorReference(string Kind, string Name, Type ReturnType, IReadOnlyList<Type> ParameterTypes, bool IsStatic)
{
    /// <summary>
    /// The complete return type when annotations cannot be represented by <see cref="ReturnType"/>.
    /// </summary>
    internal TypeSymbol? ExactReturnType { get; init; }

    /// <summary>
    /// The complete parameter types, with null where <see cref="ParameterTypes"/> is exact.
    /// </summary>
    internal IReadOnlyList<TypeSymbol?> ExactParameterTypes { get; init; } = [];
}
