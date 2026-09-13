namespace IlRepl.Engine;

/// <summary>
/// Observes asynchronous results without consuming the original awaitable twice.
/// </summary>
public static partial class ComparisonProbe
{
    /// <summary>
    /// Observes a task's completion before its caller resumes, preserving its result and exception.
    /// </summary>
    /// <param name="identity">The invocation identity.</param>
    /// <param name="receiver">The selected method's receiver.</param>
    /// <param name="arguments">The selected method's arguments.</param>
    /// <param name="task">The returned task.</param>
    /// <param name="aliases">Canonical alias groups for the receiver and arguments.</param>
    /// <returns>The task whose completion includes the observation.</returns>
    public static async Task TrackTask(int identity, object? receiver, object?[] arguments, Task task, int[] aliases)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Leave(identity, receiver, arguments, null, exception, aliases);
            throw;
        }

        Leave(identity, receiver, arguments, null, null, aliases);
    }

    /// <summary>
    /// Observes a task's typed result before its caller resumes.
    /// </summary>
    /// <typeparam name="T">The task's result type.</typeparam>
    /// <param name="identity">The invocation identity.</param>
    /// <param name="receiver">The selected method's receiver.</param>
    /// <param name="arguments">The selected method's arguments.</param>
    /// <param name="task">The returned task.</param>
    /// <param name="aliases">Canonical alias groups for the receiver and arguments.</param>
    /// <returns>The task whose completion includes the observation.</returns>
    public static async Task<T> TrackTask<T>(int identity, object? receiver, object?[] arguments, Task<T> task, int[] aliases)
    {
        T result;
        try
        {
            result = await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Leave(identity, receiver, arguments, null, exception, aliases);
            throw;
        }

        Leave(identity, receiver, arguments, result, null, aliases);
        return result;
    }

    /// <summary>
    /// Observes a value task once, before its caller resumes.
    /// </summary>
    /// <param name="identity">The invocation identity.</param>
    /// <param name="receiver">The selected method's receiver.</param>
    /// <param name="arguments">The selected method's arguments.</param>
    /// <param name="task">The returned value task.</param>
    /// <param name="aliases">Canonical alias groups for the receiver and arguments.</param>
    /// <returns>The value task whose completion includes the observation.</returns>
    public static async ValueTask TrackValueTask(int identity, object? receiver, object?[] arguments, ValueTask task, int[] aliases)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Leave(identity, receiver, arguments, null, exception, aliases);
            throw;
        }

        Leave(identity, receiver, arguments, null, null, aliases);
    }

    /// <summary>
    /// Observes a value task's typed result once, before its caller resumes.
    /// </summary>
    /// <typeparam name="T">The value task's result type.</typeparam>
    /// <param name="identity">The invocation identity.</param>
    /// <param name="receiver">The selected method's receiver.</param>
    /// <param name="arguments">The selected method's arguments.</param>
    /// <param name="task">The returned value task.</param>
    /// <param name="aliases">Canonical alias groups for the receiver and arguments.</param>
    /// <returns>The value task whose completion includes the observation.</returns>
    public static async ValueTask<T> TrackValueTask<T>(int identity, object? receiver, object?[] arguments,
        ValueTask<T> task, int[] aliases)
    {
        T result;
        try
        {
            result = await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Leave(identity, receiver, arguments, null, exception, aliases);
            throw;
        }

        Leave(identity, receiver, arguments, result, null, aliases);
        return result;
    }
}
