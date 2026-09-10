namespace IlRepl.Engine;

/// <summary>
/// Carries a stack to a successor, including the empty stack supplied by leave.
/// </summary>
internal sealed record FlowEdge(int Target, bool ClearsStack = false);
