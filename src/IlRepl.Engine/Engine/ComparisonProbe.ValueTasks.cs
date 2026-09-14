using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Preserves value-task representations without consuming a caller-owned value-task source.
/// </summary>
public static partial class ComparisonProbe
{
    /// <summary>
    /// Observes a value task without replacing it or consuming its source.
    /// </summary>
    /// <param name="identity">The invocation identity.</param>
    /// <param name="receiver">The selected method's receiver.</param>
    /// <param name="arguments">The selected method's arguments.</param>
    /// <param name="task">The original returned value task.</param>
    /// <param name="aliases">Canonical alias groups for the receiver and arguments.</param>
    /// <returns>The original value task unchanged.</returns>
    public static ValueTask TrackValueTask(int identity, object? receiver, object?[] arguments, ValueTask task, int[] aliases)
    {
        var backing = typeof(ValueTask).GetField("_obj", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(task);
        if (backing is Task pending)
        {
            TrackTask(identity, receiver, arguments, pending, aliases);
        }
        else if (backing is null)
        {
            Leave(identity, receiver, arguments, null, null, aliases);
        }
        else
        {
            TrackValueTaskSource(identity, receiver, arguments, task, aliases);
        }

        return task;
    }

    /// <summary>
    /// Observes a typed value task without replacing it or consuming its source.
    /// </summary>
    /// <typeparam name="T">The value task's result type.</typeparam>
    /// <param name="identity">The invocation identity.</param>
    /// <param name="receiver">The selected method's receiver.</param>
    /// <param name="arguments">The selected method's arguments.</param>
    /// <param name="task">The original returned value task.</param>
    /// <param name="aliases">Canonical alias groups for the receiver and arguments.</param>
    /// <returns>The original value task unchanged.</returns>
    public static ValueTask<T> TrackValueTask<T>(int identity, object? receiver, object?[] arguments, ValueTask<T> task, int[] aliases)
    {
        var backing = typeof(ValueTask<T>).GetField("_obj", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(task);
        if (backing is Task<T> pending)
        {
            TrackTask(identity, receiver, arguments, pending, aliases);
        }
        else if (backing is null)
        {
            Leave(identity, receiver, arguments, task.Result, null, aliases);
        }
        else
        {
            TrackValueTaskSource(identity, receiver, arguments, task, aliases);
        }

        return task;
    }

    private static void TrackValueTaskSource(int identity, object? receiver, object?[] arguments, object task, int[] aliases)
    {
        Leave(identity, receiver, arguments,
            Unavailable("return the source-backed value task from the scenario to observe its completion"), null, aliases);
        lock (Gate)
        {
            Invocations[identity].ReturnedValueTask = task;
            Invocations[identity].ValueTaskCompletion = (result, failure) => Leave(identity, receiver, arguments, result, failure, aliases);
        }
    }

    /// <summary>
    /// Records a source-backed value task after the worker consumes its entry point's returned awaitable once.
    /// </summary>
    /// <param name="returned">The original boxed return value.</param>
    /// <param name="result">Its awaited result.</param>
    /// <param name="failure">Its completion exception, if any.</param>
    internal static void CompleteValueTask(object? returned, object? result, Exception? failure)
    {
        lock (Gate)
        {
            foreach (var invocation in Invocations.Where(invocation => invocation.ReturnedValueTask?.Equals(returned) == true))
            {
                invocation.ValueTaskCompletion!(result, failure);
            }
        }
    }
}
