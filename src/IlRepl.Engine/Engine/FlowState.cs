using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Holds a path's stack while keeping unknown incoming paths separate from known witnesses.
/// </summary>
/// <typeparam name="T">The type representation.</typeparam>
internal sealed record FlowState<T>(FlowValue<T>[]? Values, bool HasUnknownPath = false, bool Invalid = false) where T : class
{
    /// <summary>
    /// The prescribed empty entry stack.
    /// </summary>
    public static FlowState<T> Empty { get; } = new([]);

    /// <summary>
    /// An incoming path whose stack effect could not be established.
    /// </summary>
    public static FlowState<T> Unknown { get; } = new(null, true);

    /// <summary>
    /// The presentation of this reachable state.
    /// </summary>
    public AnalyzedStackKind Kind => Invalid ? AnalyzedStackKind.Invalid
        : HasUnknownPath || Values is null ? AnalyzedStackKind.Unknown : AnalyzedStackKind.Known;
}
