using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Holds a path's stack and original-receiver state while keeping unknown paths separate.
/// </summary>
/// <typeparam name="T">The type representation.</typeparam>
internal sealed record FlowState<T>(
    FlowValue<T>[]? Values,
    bool HasUnknownPath = false,
    bool Invalid = false,
    bool ThisArgumentIsOriginal = false,
    FilterPathState[]? FilterPaths = null) where T : class
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
    /// The presentation of this reachable state.
    /// </summary>
    public AnalyzedStackKind Kind => Invalid ? AnalyzedStackKind.Invalid
        : HasUnknownPath || Values is null ? AnalyzedStackKind.Unknown : AnalyzedStackKind.Known;
}
