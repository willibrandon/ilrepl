using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Awaits built-in Task and ValueTask results without invoking a user-defined result accessor.
/// </summary>
internal static class AsyncObservation
{
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
                if (resultType.Assembly == typeof(Task).Assembly && resultType.FullName == "System.Threading.Tasks.VoidTaskResult")
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
