using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Keeps the converged states and diagnostics for one immutable body revision.
/// </summary>
/// <typeparam name="T">The type representation.</typeparam>
internal sealed record FlowResult<T>(
    FlowState<T>?[] Before,
    FlowState<T>?[] After,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics,
    int MaxStack) where T : class
{
    /// <summary>
    /// The incoming state at the synthetic end of the source body.
    /// </summary>
    public FlowState<T>? End => Before[^1];

    /// <summary>
    /// Whether the body's graph is still missing source or targets.
    /// </summary>
    public bool Incomplete => Diagnostics.Any(d => d.Kind == AnalysisDiagnosticKind.Incomplete);
}
