using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Awaits built-in Task and ValueTask results without invoking a user-defined result accessor.
/// </summary>
internal static class AsyncObservation
{
    // A task without a result reports this private type as its result type. It has no public name to refer to.
    private static readonly Type? s_voidResult = typeof(Task).Assembly.GetType("System.Threading.Tasks.VoidTaskResult");

    /// <summary>
    /// Waits for a returned task or value task and reads its result, passing any other value through.
    /// </summary>
    /// <param name="value">The value a method returned.</param>
    /// <returns>The awaited result, null for a task with no result, or the value itself when it is not a task.</returns>
    internal static async Task<object?> AwaitAsync(object? value)
    {
        if (value is ValueTask voidValueTask)
        {
            await voidValueTask.ConfigureAwait(false);
            return null;
        }

        if (value is not null && value.GetType() is { IsGenericType: true } valueType
            && valueType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            value = valueType.GetMethod(nameof(ValueTask<int>.AsTask), BindingFlags.Public | BindingFlags.Instance)!.Invoke(value, null);
        }

        if (value is not Task task)
        {
            return value;
        }

        await task.ConfigureAwait(false);
        for (var type = task.GetType(); type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = type.GetGenericArguments()[0];
                if (resultType == s_voidResult)
                {
                    return null;
                }

                return type.GetProperty(nameof(Task<int>.Result),
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.GetValue(task);
            }
        }

        return null;
    }
}
