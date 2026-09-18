namespace IlRepl.Protocol;

/// <summary>
/// Reports process ownership separately from runtime identity and execution availability.
/// </summary>
/// <param name="Epoch">The current supervisor generation.</param>
/// <param name="Restoring">Whether ownership adoption is in progress.</param>
/// <param name="Degraded">Whether automatic adoption failed and requires an explicit retry.</param>
/// <param name="Detail">The user-facing explanation, when relevant.</param>
public sealed record ProcessSupervisionState(long Epoch, bool Restoring, bool Degraded, string? Detail);
