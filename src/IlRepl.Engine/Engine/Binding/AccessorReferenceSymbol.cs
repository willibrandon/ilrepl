namespace IlRepl.Engine.Binding;

/// <summary>
/// An accessor line inside a property or event block: which accessor, and the method it names.
/// </summary>
/// <param name="Kind"><c>get</c>, <c>set</c>, <c>other</c>, <c>addon</c>, <c>removeon</c>, or <c>fire</c>.</param>
/// <param name="Name">The method name.</param>
/// <param name="ReturnType">The method's return type.</param>
/// <param name="ParameterTypes">The method's parameter types.</param>
/// <param name="IsStatic">True for a static accessor.</param>
public sealed record AccessorReferenceSymbol(
    string Kind,
    string Name,
    TypeSymbol ReturnType,
    IReadOnlyList<TypeSymbol> ParameterTypes,
    bool IsStatic);
