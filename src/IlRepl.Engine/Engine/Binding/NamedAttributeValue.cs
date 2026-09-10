namespace IlRepl.Engine.Binding;

/// <summary>
/// Associates a symbolic attribute value with a named field or property.
/// </summary>
/// <param name="Field">The named field, or null for a property.</param>
/// <param name="Property">The named property, or null for a field.</param>
/// <param name="Value">The serialized value.</param>
internal sealed record NamedAttributeValue(FieldSymbol? Field, PropertySymbol? Property, AttributeValueSymbol Value);
