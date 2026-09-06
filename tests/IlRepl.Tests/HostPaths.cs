using IlRepl.Hosting;

namespace IlRepl.Tests;

/// <summary>
/// Locates the host published into the test output.
/// </summary>
internal static class HostPaths
{
    /// <summary>
    /// The host assembly beside the test assembly.
    /// </summary>
    public static string HostAssembly { get; } = HostLocator.FindHost(AppContext.BaseDirectory);

    /// <summary>
    /// Starts a host process for a test.
    /// </summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running engine.</returns>
    public static Task<HostProcessEngine> StartEngineAsync(CancellationToken cancellationToken) =>
        HostProcessEngine.StartAsync(HostAssembly, RepoPaths.Root, cancellationToken);
}
