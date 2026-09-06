namespace IlRepl.Engine;

/// <summary>
/// A cell parameter declared with <c>.args</c>, together with the value passed when the cell runs.
/// </summary>
/// <param name="Type">The parameter type.</param>
/// <param name="Name">The parameter name, or null when unnamed.</param>
/// <param name="Value">The value passed to the cell method.</param>
/// <param name="ValueText">The literal the value was parsed from, kept for display.</param>
public sealed record ArgumentDeclaration(Type Type, string? Name, object? Value, string ValueText);
