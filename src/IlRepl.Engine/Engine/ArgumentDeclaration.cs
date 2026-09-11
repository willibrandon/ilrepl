using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// A cell parameter declared with <c>.args</c>, together with the value passed when the cell runs.
/// </summary>
/// <param name="Type">The parameter type.</param>
/// <param name="Name">The parameter name, or null when unnamed.</param>
/// <param name="Value">The value passed to the cell method.</param>
/// <param name="ValueText">The literal the value was parsed from, kept for display.</param>
public sealed record ArgumentDeclaration(Type Type, string? Name, object? Value, string ValueText)
{
    /// <summary>
    /// The exact type retained when its runtime projection cannot represent its complete shape.
    /// </summary>
    internal TypeSymbol? ExactType { get; init; }

    /// <summary>
    /// The parameter attributes written before the type: <c>[in]</c>, <c>[out]</c>, <c>[opt]</c>, and
    /// a default set with <c>.param</c>.
    /// </summary>
    public System.Reflection.ParameterAttributes Attributes { get; init; }

    /// <summary>
    /// The <c>modreq</c> types on the parameter type.
    /// </summary>
    public IReadOnlyList<Type> RequiredModifiers { get; init; } = [];

    /// <summary>
    /// The <c>modopt</c> types on the parameter type.
    /// </summary>
    public IReadOnlyList<Type> OptionalModifiers { get; init; } = [];

    /// <summary>
    /// The default value set with <c>.param [N] = value</c>, or null.
    /// </summary>
    public object? DefaultValue { get; init; }

    /// <summary>
    /// True when <c>.param</c> gave the parameter a default; the runtime does not use it to fill an argument.
    /// </summary>
    public bool HasDefault { get; init; }

    /// <summary>
    /// The custom attributes declared on the parameter.
    /// </summary>
    public IReadOnlyList<CustomAttributeDeclaration> CustomAttributes { get; init; } = [];
}
