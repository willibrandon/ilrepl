namespace IlRepl.Engine;

/// <summary>
/// Retains one filter path's stack, variables, pending unwind, and argument-zero identity.
/// </summary>
/// <param name="Values">The stack values.</param>
/// <param name="Locals">Values stored in local slots.</param>
/// <param name="Arguments">Values stored in argument slots.</param>
/// <param name="ThisArgumentIsOriginal">Whether argument zero is the original receiver.</param>
/// <param name="PendingUnwindEffect">The deferred unwind effect.</param>
/// <param name="StackUnknown">Whether the stack shape is unknown.</param>
/// <param name="ReceiverConditions">Path conditions keyed by receiver source.</param>
/// <param name="CorrelatedAlternatives">Exact path alternatives retained in a bounded slot.</param>
/// <param name="ConstructorState">The initialization state of a reference-type constructor receiver on this path.</param>
internal sealed record FilterPathState(
    FilterPathValue[] Values,
    IReadOnlyDictionary<int, FilterPathValue>? Locals,
    IReadOnlyDictionary<int, FilterPathValue>? Arguments,
    bool ThisArgumentIsOriginal,
    PendingUnwindEffect? PendingUnwindEffect = null,
    bool StackUnknown = false,
    IReadOnlyDictionary<int, FilterPathValue>? ReceiverConditions = null,
    FilterPathState[]? CorrelatedAlternatives = null,
    ConstructorThisState ConstructorState = ConstructorThisState.NotTracked);
