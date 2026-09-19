namespace IlRepl.Protocol;

/// <summary>
/// The inputs and result of one selected-method invocation within a comparison scenario.
/// </summary>
/// <param name="Inputs">The receiver and arguments captured immediately before the call.</param>
/// <param name="Outputs">The returned value, receiver, and arguments captured after completion.</param>
/// <param name="Exception">The selected method's exception, or null.</param>
public sealed record InvocationObservation(
    IReadOnlyList<ObservedMember> Inputs,
    IReadOnlyList<ObservedMember> Outputs,
    ObservedException? Exception);
