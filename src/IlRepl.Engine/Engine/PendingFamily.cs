using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// A family replayed during a rebuild, waiting to be written with the rest of the group.
/// </summary>
internal sealed record PendingFamily(
    TypeDeclaration Declaration,
    IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)> Prototypes,
    SessionType? Previous);
