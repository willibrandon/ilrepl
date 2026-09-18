namespace IlRepl.Protocol;

/// <summary>
/// Carries the frontend's authoritative scope registry to one supervisor generation.
/// </summary>
/// <param name="Epoch">The supervisor generation receiving the snapshot.</param>
/// <param name="Revision">The monotonically increasing registry revision.</param>
/// <param name="Scopes">The groups whose ownership must be acknowledged.</param>
public sealed record SupervisorSnapshot(long Epoch, long Revision, OwnedProcessScope[] Scopes);
