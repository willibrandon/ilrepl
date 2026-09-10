namespace IlRepl.Engine.Binding;

/// <summary>
/// A bound argument declaration whose value remains unevaluated until actual submission.
/// </summary>
/// <param name="Type">The declared type.</param>
/// <param name="Name">The optional argument name.</param>
/// <param name="Literal">The initializer text, or null for a default value.</param>
public sealed record ArgumentSyntax(TypeSymbol Type, string? Name, string? Literal);
