namespace IlRepl.Engine;

/// <summary>
/// What a read produced: the instructions decoded, and the faults that stopped decoding or that a
/// later check found. A damaged tail never hides a readable head.
/// </summary>
/// <param name="Instructions">The instructions, in offset order.</param>
/// <param name="Problems">The faults, each naming an offset; empty for a well-formed body.</param>
public sealed record IlReadResult(IReadOnlyList<RawInstruction> Instructions, IReadOnlyList<string> Problems);
