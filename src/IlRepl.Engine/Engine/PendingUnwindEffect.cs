namespace IlRepl.Engine;

/// <summary>
/// Retains the receiver transformations and handler entries for a deferred exception unwind.
/// </summary>
/// <param name="Transformations">The possible receiver transformations.</param>
/// <param name="HandlerEntries">The receiver states entering each handler.</param>
/// <param name="BoundOutputs">Transformations already applied to their incoming paths.</param>
/// <param name="BoundHandlerEntries">Handler states already applied to their incoming paths.</param>
/// <param name="CorrelationLost">Whether later writes made the bound states stale.</param>
internal sealed record PendingUnwindEffect(
    FilterPathState[] Transformations,
    IReadOnlyDictionary<int, FilterPathState[]> HandlerEntries,
    FilterPathState[]? BoundOutputs = null,
    IReadOnlyDictionary<int, FilterPathState[]>? BoundHandlerEntries = null,
    bool CorrelationLost = false);
