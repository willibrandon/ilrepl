using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Retains an immutable method publication after the EventPipe callback buffer is reused.
/// </summary>
/// <param name="ModuleId">The runtime module identity.</param>
/// <param name="Token">The original metadata token.</param>
/// <param name="Assembly">The assembly identity observed through loader events.</param>
/// <param name="Compilation">The published method version.</param>
internal sealed record NativeCodeEvent(ulong ModuleId, int Token, string Assembly, NativeCompilation Compilation);
