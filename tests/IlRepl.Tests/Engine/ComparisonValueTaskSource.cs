namespace IlRepl.Tests.Engine;

/// <summary>
/// Supplies an actual async iterator disposal operation backed by a non-generic value-task source.
/// </summary>
public static class ComparisonValueTaskSource
{
    /// <summary>
    /// The original disposal operation returned by the iterator.
    /// </summary>
    public static ValueTask Last { get; private set; }

    /// <summary>
    /// Starts asynchronous iterator disposal after reaching its first element.
    /// </summary>
    /// <returns>The iterator's original disposal value task.</returns>
    public static ValueTask Start()
    {
        var iterator = Values().GetAsyncEnumerator();
        if (!iterator.MoveNextAsync().GetAwaiter().GetResult())
        {
            throw new InvalidOperationException("the iterator did not yield its value");
        }

        Last = iterator.DisposeAsync();
        return Last;
    }

    private static async IAsyncEnumerable<int> Values()
    {
        try
        {
            yield return 42;
        }
        finally
        {
            await Task.Yield();
        }
    }
}
