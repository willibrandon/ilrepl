using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Retains an unconfirmed candidate's symbolic target and ordering facts until its page is requested.
/// </summary>
internal sealed record OperandCandidate
{
    /// <summary>
    /// The complete ordering facts, computed before paging or insertion confirmation.
    /// </summary>
    public required CandidateRankFacts Rank { get; init; }

    /// <summary>
    /// The candidate's source kind.
    /// </summary>
    public required CompletionKind Kind { get; init; }

    /// <summary>
    /// The selected type, generic definition or type argument.
    /// </summary>
    public TypeSymbol? Type { get; init; }

    /// <summary>
    /// The selected method or constructor.
    /// </summary>
    public MethodSymbol? Method { get; init; }

    /// <summary>
    /// The selected field.
    /// </summary>
    public FieldSymbol? Field { get; init; }

    /// <summary>
    /// The selected local or argument index, or -1 for other candidates.
    /// </summary>
    public int Slot { get; init; } = -1;

    /// <summary>
    /// Whether the candidate opens a generic argument list for its retained definition.
    /// </summary>
    public bool StartsGeneric { get; init; }

    /// <summary>
    /// The generic definition whose argument constraints this candidate satisfies.
    /// </summary>
    public GenericCompletionTarget? GenericOwner { get; init; }
}
