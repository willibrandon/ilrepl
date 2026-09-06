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
    /// Builds the samples before any test runs.
    /// </summary>
    /// <param name="context">The assembly initialization context.</param>
    /// <returns>A task that completes when the samples are built.</returns>
    [AssemblyInitialize]
    public static async Task AssemblyInitialize(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Samples = new SampleFixture();
        await Samples.InitializeAsync().ConfigureAwait(false);
    }
}
