using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Describes one source entry without retaining runtime-specific instruction objects.
/// </summary>
/// <typeparam name="T">The type representation.</typeparam>
internal sealed record FlowNode<T>(AnalysisLocation Location, string Source) where T : class
{
    /// <summary>
    /// The real source coordinates, or null when Location supplies only an internal entry identity.
    /// </summary>
    public AnalysisLocation? SourceLocation { get; init; } = Location;

    /// <summary>
    /// Identifies whether this source can be navigated in the current editor snapshot.
    /// </summary>
    public AnalysisSourceKind SourceKind { get; init; } = Location.Offset is not null ? AnalysisSourceKind.Imported
        : Location.Line >= 0 ? AnalysisSourceKind.Document : AnalysisSourceKind.Accepted;

    /// <summary>
    /// The instruction's shared operand facts, or null for a source boundary.
    /// </summary>
    public StackOperandView<T>? Instruction { get; init; }

    /// <summary>
    /// The resolved instruction signature, retaining the original source separately for navigation.
    /// </summary>
    public string? InstructionSyntax { get; init; }

    /// <summary>
    /// Whether this entry represents an implicit transition rather than a typed instruction.
    /// </summary>
    public bool Synthetic { get; init; }

    /// <summary>
    /// Labels attached to this entry.
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>
    /// Explicit branch targets.
    /// </summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>
    /// The structured exception boundary, if any.
    /// </summary>
    public BlockKind? Block { get; init; }

    /// <summary>
    /// The exception object supplied by a catch boundary.
    /// </summary>
    public T? CatchType { get; init; }

    /// <summary>
    /// An explicit exception clause associated with this source entry.
    /// </summary>
    public ExceptionRegion<T>? ExceptionRegion { get; init; }

    /// <summary>
    /// Whether binding or decoding left this entry's effect unresolved.
    /// </summary>
    public bool EffectUnknown { get; init; }
}
