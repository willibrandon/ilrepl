namespace IlRepl.Tests.Engine;

/// <summary>
/// A real generic task subclass supplied by an external assembly to comparison workers.
/// </summary>
/// <typeparam name="T">The task result type.</typeparam>
public sealed class ComparisonDerivedTask<T> : Task<T>
{
    /// <summary>
    /// Creates a pending task using the supplied result callback.
    /// </summary>
    /// <param name="action">The work performed when the scenario starts the task.</param>
    public ComparisonDerivedTask(Func<T> action) : base(action)
    {
    }

    /// <summary>
    /// Throws if observation invokes a user accessor instead of reading the framework task result.
    /// </summary>
    public new T Result => throw new InvalidOperationException("user result getter must not run");
}
