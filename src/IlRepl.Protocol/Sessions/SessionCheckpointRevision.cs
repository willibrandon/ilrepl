namespace IlRepl.Protocol;

/// <summary>
/// Identifies unchanged prefixes in an ordered source checkpoint so only changed tails cross the connection.
/// </summary>
/// <param name="Sequence">The strictly increasing revision within one host connection.</param>
/// <param name="EntriesKept">The unchanged source-transition prefix.</param>
/// <param name="CellsKept">The unchanged historical-cell prefix.</param>
/// <param name="ReferencesKept">The unchanged dependency-manifest prefix.</param>
/// <param name="AssetsKept">The unchanged embedded-image prefix.</param>
public sealed record SessionCheckpointRevision(long Sequence, int EntriesKept, int CellsKept, int ReferencesKept, int AssetsKept);
