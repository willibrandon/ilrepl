using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// The inputs and live completion values of one invocation inside an isolated comparison runtime.
/// </summary>
internal sealed class PendingInvocation(IReadOnlyList<ObservedMember> inputs)
{
    /// <summary>
    /// The captured input graph before the selected method began.
    /// </summary>
    internal IReadOnlyList<ObservedMember> Inputs { get; } = inputs;

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
}
