namespace IlRepl.Engine.Binding;

/// <summary>
/// Represents a bound type with its pinned annotation and custom modifiers kept separately.
/// </summary>
/// <remarks>
/// A type bound from syntax, with the annotations written after it kept apart: whether it was
/// <c>pinned</c>, and the custom modifiers on it.
/// </remarks>
/// <param name="Type">The type.</param>
/// <param name="Pinned">True when the type carried the <c>pinned</c> modifier.</param>
/// <param name="RequiredModifiers">The <c>modreq</c> types, in order.</param>
/// <param name="OptionalModifiers">The <c>modopt</c> types, in order.</param>
public sealed record BoundType(TypeSymbol Type, bool Pinned, IReadOnlyList<TypeSymbol> RequiredModifiers,
    IReadOnlyList<TypeSymbol> OptionalModifiers);
