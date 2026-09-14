namespace IlRepl.Engine;

/// <summary>
/// Observes asynchronous task results while retaining their identity and completion behavior.
/// </summary>
public static partial class ComparisonProbe
{
    /// <summary>
    /// Records a task's completion without replacing the task returned to its caller.
    /// </summary>
    /// <param name="identity">The invocation identity.</param>
    /// <param name="receiver">The selected method's receiver.</param>
    /// <param name="arguments">The selected method's arguments.</param>
    /// <param name="task">The returned task.</param>
    /// <param name="aliases">Canonical alias groups for the receiver and arguments.</param>
    /// <returns>The original task with its identity, state, and completion behavior intact.</returns>
    public static Task TrackTask(int identity, object? receiver, object?[] arguments, Task task, int[] aliases)
    {
        TrackCompletion(identity, task, () =>
        {
            Exception? failure = null;
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Leave(identity, receiver, arguments, null, failure, aliases);
        });
        return task;
    }

    /// <summary>
    /// Records a task's typed result without replacing the task returned to its caller.
    /// </summary>
    /// <typeparam name="T">The task's result type.</typeparam>
    /// <param name="identity">The invocation identity.</param>
    /// <param name="receiver">The selected method's receiver.</param>
    /// <param name="arguments">The selected method's arguments.</param>
    /// <param name="task">The returned task.</param>
    /// <param name="aliases">Canonical alias groups for the receiver and arguments.</param>
    /// <returns>The original task with its identity, state, and completion behavior intact.</returns>
    public static Task<T> TrackTask<T>(int identity, object? receiver, object?[] arguments, Task<T> task, int[] aliases)
    {
        TrackCompletion(identity, task, () =>
        {
            object? result = null;
            Exception? failure = null;
            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Leave(identity, receiver, arguments, result, failure, aliases);
        });
        return task;
    }

    private static void TrackCompletion(int identity, Task task, Action observe)
    {
        lock (Gate)
        {
            var invocation = Invocations[identity];
            invocation.Awaitable = task;
            invocation.Completion = task.ContinueWith(_ => observe(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
