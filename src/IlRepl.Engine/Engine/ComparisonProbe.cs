using System.Globalization;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Records selected-method boundaries exclusively inside a fresh comparison runtime.
/// </summary>
public static partial class ComparisonProbe
{
    private static readonly Lock Gate = new();
    private static readonly List<PendingInvocation> Invocations = [];
    private static readonly NullReferenceObservation NullReferenceValue = new();
    private static readonly NullTaskObservation NullTaskValue = new();
    private static IReadOnlyDictionary<string, string> s_typeNames = new Dictionary<string, string>();

    /// <summary>
    /// Records the receiver and argument graph immediately before a selected-method call.
    /// </summary>
    /// <param name="receiver">The boxed receiver, or null for a static method.</param>
    /// <param name="arguments">The fixed arguments, dereferenced when passed by reference.</param>
    /// <param name="aliases">Canonical alias groups for managed reference slots, or null when none are supplied.</param>
    /// <returns>The invocation identity supplied to its completion boundary.</returns>
    public static int Enter(object? receiver, object?[] arguments, int[]? aliases = null)
    {
        lock (Gate)
        {
            var observation = new StructuralObservation(s_typeNames);
            var roots = Roots(observation, receiver, arguments, aliases);
            var identity = Invocations.Count;
            Invocations.Add(new PendingInvocation(roots, observation.Identities));
            return identity;
        }
    }

    /// <summary>
    /// Records a selected method's return boundary without replacing its result or exception.
    /// </summary>
    /// <param name="identity">The invocation identity returned by Enter.</param>
    /// <param name="receiver">The receiver after the call.</param>
    /// <param name="arguments">The arguments after the call, including ref/out writes.</param>
    /// <param name="result">The return value or awaitable.</param>
    /// <param name="exception">The selected method's synchronous exception, or null.</param>
    /// <param name="aliases">Canonical alias groups after the call, including its managed reference return.</param>
    public static void Leave(
        int identity,
        object? receiver,
        object?[] arguments,
        object? result,
        Exception? exception,
        int[]? aliases = null)
    {
        lock (Gate)
        {
            var invocation = Invocations[identity];
            var observation = new StructuralObservation(s_typeNames, new ObservationIdentityMap(invocation.InputIdentities));
            var outputs = Roots(observation, receiver, arguments, aliases);
            outputs.Add(new ObservedMember("return", observation.Capture(result)));
            invocation.Observation = new InvocationObservation(invocation.Inputs, outputs,
                exception is null ? null : observation.Exception(exception));
        }
    }

    /// <summary>
    /// Marks an argument or return value that cannot be boxed for a structural observation.
    /// </summary>
    /// <param name="reason">The reason the scenario must provide an explicit observation.</param>
    /// <returns>The marker included in the observation graph.</returns>
    public static object Unavailable(string reason) => new UnavailableObservation(reason);

    /// <summary>
    /// Marks a null managed reference without attempting to read through it.
    /// </summary>
    /// <returns>The marker distinguishing a null reference from a reference to a null value.</returns>
    public static object NullReference() => NullReferenceValue;

    /// <summary>
    /// Marks a null task return without replacing the value returned to its caller.
    /// </summary>
    /// <returns>The marker distinguishing a null task from a completed task's result.</returns>
    public static object NullTask() => NullTaskValue;

    /// <summary>
    /// Initializes recording in an otherwise fresh worker runtime before loading user code.
    /// </summary>
    /// <param name="typeNames">The generated-to-source type identity mapping.</param>
    internal static void Initialize(IReadOnlyDictionary<string, string> typeNames)
    {
        lock (Gate)
        {
            Invocations.Clear();
            s_typeNames = typeNames;
        }
    }

    /// <summary>
    /// Finishes observations for completed tasks and returns each invocation snapshot.
    /// </summary>
    /// <returns>The ordered invocation observations.</returns>
    internal static async Task<IReadOnlyList<InvocationObservation>> CompleteAsync()
    {
        Task[] completions;
        lock (Gate)
        {
            if (Invocations.Any(invocation => invocation.Awaitable is { IsCompleted: false }))
            {
                throw new ReplException("a selected-method invocation was still running when its scenario completed");
            }

            completions = [.. Invocations.Select(invocation => invocation.Completion).OfType<Task>()];
        }

        await Task.WhenAll(completions).ConfigureAwait(false);
        lock (Gate)
        {
            return Invocations.Select(invocation => invocation.Observation
                ?? throw new ReplException("a selected-method invocation was still running when its scenario completed")).ToArray();
        }
    }

    private static List<ObservedMember> Roots(StructuralObservation observation, object? receiver, object?[] arguments, int[]? aliases)
    {
        var roots = new List<ObservedMember> { new("receiver", observation.Capture(receiver)) };
        for (var index = 0; index < arguments.Length; index++)
        {
            roots.Add(new ObservedMember("argument " + index.ToString(CultureInfo.InvariantCulture),
                observation.Capture(arguments[index])));
        }

        if (aliases?.Any(alias => alias >= 0) == true)
        {
            roots.Add(new ObservedMember("reference aliases", new ObservedValue("scalar", "managed references",
                string.Join(",", aliases.Select(alias => alias.ToString(CultureInfo.InvariantCulture))), null, [])));
        }

        return roots;
    }
}
