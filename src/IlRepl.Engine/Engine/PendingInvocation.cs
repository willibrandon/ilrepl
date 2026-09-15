using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// The inputs and live completion values of one invocation inside an isolated comparison runtime.
/// </summary>
/// <param name="inputs">The captured input graph.</param>
/// <param name="identities">The weak identities that link later snapshots to those inputs.</param>
internal sealed class PendingInvocation(IReadOnlyList<ObservedMember> inputs, ObservationIdentityMap identities)
{
    /// <summary>
    /// The captured input graph before the selected method began.
    /// </summary>
    internal IReadOnlyList<ObservedMember> Inputs { get; } = inputs;

    /// <summary>
    /// The input reference identities retained weakly until the method completes.
    /// </summary>
    internal ObservationIdentityMap InputIdentities { get; } = identities;

    /// <summary>
    /// The completed observation, or null while the selected invocation is still running.
    /// </summary>
    internal InvocationObservation? Observation { get; set; }

    /// <summary>
    /// The original returned task, or null for an invocation without separate task tracking.
    /// </summary>
    internal Task? Awaitable { get; set; }

    /// <summary>
    /// The observation callback to finish after the original task completes.
    /// </summary>
    internal Task? Completion { get; set; }

    /// <summary>
    /// The source-backed value task that only its caller may consume.
    /// </summary>
    internal object? ReturnedValueTask { get; set; }

    /// <summary>
    /// Records the result if the worker awaits this value task as its entry point's return value.
    /// </summary>
    internal Action<object?, Exception?>? ValueTaskCompletion { get; set; }
}
