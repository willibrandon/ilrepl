namespace IlRepl.Tests.Engine;

/// <summary>
/// Supplies real tasks with observable identity, state, and controlled completion inside comparison workers.
/// </summary>
public static class ComparisonTaskSource
{
    private static readonly Task<int> CachedTask = WithState("state");
    private static readonly TaskCompletionSource<int> PendingSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Returns the same completed task on every call.
    /// </summary>
    /// <returns>The cached task containing 42.</returns>
    public static Task<int> Cached() => CachedTask;

    /// <summary>
    /// Returns a distinct completed task on every call.
    /// </summary>
    /// <returns>A new task containing 42.</returns>
    public static Task<int> Fresh() => WithState("state");

    /// <summary>
    /// Creates a completed task with caller-visible state.
    /// </summary>
    /// <param name="state">The task's asynchronous state.</param>
    /// <returns>A completed task retaining the supplied state.</returns>
    public static Task<int> WithState(string state)
    {
        var completion = new TaskCompletionSource<int>(state);
        completion.SetResult(42);
        return completion.Task;
    }

    /// <summary>
    /// Returns the worker's task before its scenario completes it.
    /// </summary>
    /// <returns>The task whose continuations run asynchronously.</returns>
    public static Task<int> Pending() => PendingSource.Task;

    /// <summary>
    /// Completes the pending task after its identity has been checked by the scenario.
    /// </summary>
    /// <param name="outcome">Zero succeeds, one faults, and two cancels the task.</param>
    public static void Complete(int outcome)
    {
        switch (outcome)
        {
            case 0:
                PendingSource.SetResult(42);
                break;
            case 1:
                PendingSource.SetException(new InvalidOperationException("task failure"));
                break;
            case 2:
                PendingSource.SetCanceled(new CancellationToken(canceled: true));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }
}
