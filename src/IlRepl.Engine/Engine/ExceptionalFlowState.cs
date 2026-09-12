namespace IlRepl.Engine;

/// <summary>
/// Adds exceptional control-flow state only to paths that need it.
/// </summary>
internal sealed record ExceptionalFlowState<T> : FlowState<T> where T : class
{
    internal ExceptionalFlowState(FlowState<T> state, IReadOnlyList<int>? pendingUnwindHandlers,
        int? syntheticHandler)
        : base(state.Values, state.HasUnknownPath, state.Invalid, state.ThisArgumentIsOriginal,
            state.FilterPaths, state.ConstructorState)
    {
        PendingUnwindHandlers = pendingUnwindHandlers;
        SyntheticHandler = syntheticHandler;
    }

    /// <summary>
    /// Gets the unwind handlers still pending on this path.
    /// </summary>
    public override IReadOnlyList<int>? PendingUnwindHandlers { get; }

    /// <summary>
    /// Gets the isolated handler whose synthetic entry produced this path.
    /// </summary>
    public override int? SyntheticHandler { get; }
}
