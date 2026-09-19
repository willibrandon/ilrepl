namespace IlRepl.Engine;

/// <summary>
/// Retains a stack value's type, producers, and receiver provenance across branches.
/// </summary>
/// <typeparam name="T">The runtime or symbolic type representation.</typeparam>
/// <param name="Type">The stack type.</param>
/// <param name="Origins">The instructions that produced the value.</param>
/// <param name="IsThis">Whether the value is the original receiver.</param>
/// <param name="IsReadOnly">Whether the address prohibits writes.</param>
/// <param name="IsKnownZero">Whether every incoming path proves an integer zero.</param>
internal sealed record FlowValue<T>(
    T? Type,
    int[] Origins,
    bool IsThis = false,
    bool IsReadOnly = false,
    bool IsKnownZero = false) where T : class;
