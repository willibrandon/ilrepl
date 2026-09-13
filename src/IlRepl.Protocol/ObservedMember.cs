namespace IlRepl.Protocol;

/// <summary>
/// A named root, field, or array element in a structural observation.
/// </summary>
/// <param name="Name">The field identity, root name, or array index.</param>
/// <param name="Value">The structurally observed value.</param>
public sealed record ObservedMember(string Name, ObservedValue Value);
