namespace IlRepl.Tests.Protocol;

/// <summary>
/// Coordinates a real submitted user method without relying on timing or process-global test state.
/// </summary>
public sealed class ExecutionPermit : IDisposable
{
    private readonly ManualResetEventSlim _release = new();

    /// <summary>
    /// Completes when user IL has entered the blocking method on its execution thread.
    /// </summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Blocks the real user method until the test explicitly releases it.
    /// </summary>
    public int Wait()
    {
        Entered.TrySetResult();
        _release.Wait();
        return 42;
    }

    /// <summary>
    /// Allows the blocked user method to return normally.
    /// </summary>
    public void Release() => _release.Set();

    /// <summary>
    /// Releases synchronization resources after the user method has returned.
    /// </summary>
    public void Dispose() => _release.Dispose();
}
