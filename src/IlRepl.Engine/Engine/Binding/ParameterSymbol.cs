namespace IlRepl.Engine.Binding;

/// <summary>
/// One parameter of a method or a signature: its type, its name when it has one, and the custom
/// modifiers written on it.
/// </summary>
/// <param name="Type">The parameter type.</param>
/// <param name="Name">The parameter name, or null.</param>
public sealed record ParameterSymbol(TypeSymbol Type, string? Name)
{
    /// <summary>
    /// The parameter's in, out and optional metadata attributes.
    /// </summary>
    public System.Reflection.ParameterAttributes Attributes { get; init; }

    /// <summary>
    /// The <c>modreq</c> types, in order.
    /// </summary>
    public IReadOnlyList<TypeSymbol> RequiredModifiers { get; init; } = [];

    /// <summary>
    /// The <c>modopt</c> types, in order.
    /// </summary>
    public IReadOnlyList<TypeSymbol> OptionalModifiers { get; init; } = [];
}
