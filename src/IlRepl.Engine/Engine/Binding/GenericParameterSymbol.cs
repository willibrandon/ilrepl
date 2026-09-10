using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// A generic parameter as its owner declares it: name, position, special constraints, and type constraints.
/// </summary>
/// <param name="Owner">The type or method definition that declares it.</param>
/// <param name="IsMethodParameter">True for a method's parameter, <c>!!N</c>; false for a type's, <c>!N</c>.</param>
/// <param name="Position">The position among the owner's parameters.</param>
/// <param name="Name">The declared name.</param>
/// <param name="Attributes">The variance and special constraints.</param>
/// <param name="Constraints">The type constraints, in declaration order.</param>
public sealed record GenericParameterSymbol(DefinitionId Owner, bool IsMethodParameter, int Position, string Name,
    GenericParameterAttributes Attributes, IReadOnlyList<TypeSymbol> Constraints)
{
    /// <summary>
    /// The parameter as a type, for signatures that mention it.
    /// </summary>
    public TypeSymbol AsType => TypeSymbol.Parameter(Owner, IsMethodParameter, Position, Name, Attributes);

    /// <summary>
    /// True for the <c>class</c> constraint.
    /// </summary>
    public bool HasReferenceTypeConstraint => Attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint);

    /// <summary>
    /// True for the <c>valuetype</c> constraint.
    /// </summary>
    public bool HasValueTypeConstraint => Attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint);

    /// <summary>
    /// True for the <c>.ctor</c> constraint.
    /// </summary>
    public bool HasDefaultConstructorConstraint => Attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint);
}
