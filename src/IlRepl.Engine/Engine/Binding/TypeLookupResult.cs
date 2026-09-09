namespace IlRepl.Engine.Binding;

/// <summary>
/// What a scope found for a written type name.
/// </summary>
/// <param name="Type">The definition, or a placeholder for one declared later in the family being written.</param>
/// <param name="FromSession">True when the session's own type table answered, before any assembly was searched.</param>
public readonly record struct TypeLookupResult(TypeSymbol Type, bool FromSession);
