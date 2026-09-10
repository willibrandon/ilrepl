namespace IlRepl.Engine;

/// <summary>
/// Retains a stack value's type, producers, and receiver provenance across branches.
/// </summary>
/// <typeparam name="T">The runtime or symbolic type representation.</typeparam>
internal sealed record FlowValue<T>(T? Type, int[] Origins, bool IsThis = false, bool IsReadOnly = false) where T : class;
