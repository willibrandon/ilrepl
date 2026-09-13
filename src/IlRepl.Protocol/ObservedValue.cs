namespace IlRepl.Protocol;

/// <summary>
/// A structural value observation that preserves typed scalar bits, cycles, and shared references.
/// </summary>
/// <param name="Kind">Null, scalar, object, array, reference, or unavailable.</param>
/// <param name="Type">The logical metadata type identity.</param>
/// <param name="Value">A scalar representation or an explanation for an unavailable observation.</param>
/// <param name="Identity">The object identity within this observation graph, or null for a value.</param>
/// <param name="Members">The observed fields or array elements in deterministic order.</param>
public sealed record ObservedValue(string Kind, string Type, string? Value, int? Identity, IReadOnlyList<ObservedMember> Members);
