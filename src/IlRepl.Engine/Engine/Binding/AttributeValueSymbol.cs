namespace IlRepl.Engine.Binding;

/// <summary>
/// Retains an attribute value as a scalar, a type symbol or a list of symbolic element values.
/// </summary>
/// <param name="Type">The serialized value type.</param>
/// <param name="Value">The scalar, type symbol, element list or null.</param>
internal sealed record AttributeValueSymbol(TypeSymbol Type, object? Value);
