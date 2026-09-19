namespace IlRepl.Protocol;

/// <summary>
/// The observations or execution failure reported by one isolated runtime.
/// </summary>
/// <param name="Outcome">Completed, cancelled, timeout, crashed, output-limit, or setup-failed.</param>
/// <param name="Invocations">The selected-method calls observed in execution order.</param>
/// <param name="Result">The direct invocation or scenario return value.</param>
/// <param name="Exception">The unhandled exception from the invocation or scenario.</param>
/// <param name="StandardOutput">The captured standard output.</param>
/// <param name="StandardError">The captured standard error.</param>
/// <param name="Detail">A failure explanation, or null.</param>
public sealed record ComparisonSide(
    string Outcome,
    IReadOnlyList<InvocationObservation> Invocations,
    ObservedValue? Result,
    ObservedException? Exception,
    string StandardOutput,
    string StandardError,
    string? Detail);
