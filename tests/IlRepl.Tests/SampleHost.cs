namespace IlRepl.Tests;

/// <summary>
/// Owns the shared <see cref="SampleFixture"/> for the test assembly.
/// </summary>
[TestClass]
public static class SampleHost
{
    /// <summary>
    /// The built samples.
    /// </summary>
    internal static SampleFixture Samples { get; private set; } = null!;

    /// <summary>
    /// Provides worker capacity for parallel terminal pumps and builds the samples before any test runs.
    /// </summary>
    /// <param name="context">The assembly initialization context.</param>
    /// <returns>A task that completes when the samples are built.</returns>
    [AssemblyInitialize]
    public static async Task AssemblyInitialize(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Each parallel terminal needs its input pump, output pump, render loop, and test continuations to make progress.
        // Reserve that capacity up front so blocked pumps do not wait for the pool's slow starvation recovery.
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, Environment.ProcessorCount * 4), completionPorts);
        Samples = new SampleFixture();
        await Samples.InitializeAsync().ConfigureAwait(false);
    }
}
