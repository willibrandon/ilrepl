namespace IlRepl.Engine;

/// <summary>
/// A session method replayed during a rebuild, waiting to be compiled with the rest of the group.
/// </summary>
internal sealed record PendingMethod(
    MethodSignature Signature, string HeaderLine, IReadOnlyList<string> BodyLines, CellState State, SessionMethod? Previous);
