using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Holds a path's stack and original-receiver state while keeping unknown paths separate.
/// </summary>
/// <typeparam name="T">The type representation.</typeparam>
/// <param name="Values">The stack values, or null when the stack is unknown.</param>
/// <param name="HasUnknownPath">Whether an unknown predecessor reaches this state.</param>
/// <param name="Invalid">Whether the state is structurally invalid.</param>
/// <param name="ThisArgumentIsOriginal">Whether argument zero is the original receiver.</param>
/// <param name="FilterPaths">The correlated filter and receiver paths.</param>
/// <param name="ConstructorState">The possible initialization states of a reference-type constructor receiver.</param>
internal record FlowState<T>(
    FlowValue<T>[]? Values,
    bool HasUnknownPath = false,
    bool Invalid = false,
    bool ThisArgumentIsOriginal = false,
    FilterPathState[]? FilterPaths = null,
    ConstructorThisState ConstructorState = ConstructorThisState.NotTracked) where T : class
{
    /// <summary>
    /// The empty entry stack outside an instance member.
    /// </summary>
    public static FlowState<T> Empty { get; } = new([]);

    /// <summary>
    /// The empty entry stack whose argument zero still holds the original receiver.
    /// </summary>
    public static FlowState<T> ThisEntry { get; } = new([], ThisArgumentIsOriginal: true);

    /// <summary>
    /// Gets the unwind handlers still pending on this path.
    /// </summary>
    public virtual IReadOnlyList<int>? PendingUnwindHandlers => null;

    /// <summary>
    /// Gets the isolated handler whose synthetic entry produced this path.
    /// </summary>
    public virtual int? SyntheticHandler => null;

    /// <summary>
    /// The presentation of this reachable state.
    /// </summary>
    public AnalyzedStackKind Kind => Invalid ? AnalyzedStackKind.Invalid
        : HasUnknownPath || Values is null ? AnalyzedStackKind.Unknown : AnalyzedStackKind.Known;
}
