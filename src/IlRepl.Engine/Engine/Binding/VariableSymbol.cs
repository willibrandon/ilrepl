namespace IlRepl.Engine.Binding;

/// <summary>
/// A local or an argument slot: its type and its name, when it has one.
/// </summary>
/// <param name="Type">The slot type.</param>
/// <param name="Name">The name, or null for an unnamed slot.</param>
/// <param name="IsPinned">True for a pinned local.</param>
public sealed record VariableSymbol(TypeSymbol Type, string? Name, bool IsPinned);
