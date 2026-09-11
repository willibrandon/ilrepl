namespace IlRepl.Engine;

/// <summary>
/// Retains one filter path's stack, variables, and current argument-zero identity.
/// </summary>
internal sealed record FilterPathState(
    FilterPathValue[] Values,
    IReadOnlyDictionary<int, FilterPathValue>? Locals,
    IReadOnlyDictionary<int, FilterPathValue>? Arguments,
    bool ThisArgumentIsOriginal);
