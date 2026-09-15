namespace IlRepl.Engine;

/// <summary>
/// An explicit marker for an invocation value the runtime cannot box or inspect faithfully.
/// </summary>
/// <param name="Reason">Why a scenario must supply its own observation.</param>
internal sealed record UnavailableObservation(string Reason);
