namespace IlRepl.Engine;

/// <summary>
/// Carries a stack to a successor, including the empty stack supplied by leave.
/// </summary>
/// <param name="Target">The successor's node index.</param>
/// <param name="ClearsStack">Whether the edge supplies an empty stack.</param>
/// <param name="IsExplicit">Whether an instruction names the target.</param>
/// <param name="SwitchCases">The switch case indexes carried by this edge.</param>
internal sealed record FlowEdge(int Target, bool ClearsStack = false, bool IsExplicit = false,
    IReadOnlyList<int>? SwitchCases = null);
