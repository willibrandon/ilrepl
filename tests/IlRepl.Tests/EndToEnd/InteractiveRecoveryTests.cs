namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Exercises the ordinary managed frontend in a real pseudoterminal through explicit runtime replacement and immediate editing.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class InteractiveRecoveryTests
{
    /// <summary>
    /// Supplies cancellation to the frontend process and actual keyboard interactions.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The first typed character paints after recovery, retained methods execute, and interrupted history remains saveable.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Restart_ImmediateTypingAndRetainedDefinitionsWork()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-terminal-recovery-").FullName;
        try
        {
            await PackagedSmoke.InterruptAndRestartAsync(RepoPaths.FrontEndAssembly, directory, TestContext.CancellationToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Quitting never leaves the terminal holding its screen, which Ghostty would show as a frozen view for about a second.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Quit_ReleasesTheScreen()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-terminal-quit-").FullName;
        try
        {
            await PackagedSmoke.QuitReleasesTheScreenAsync(RepoPaths.FrontEndAssembly, directory, TestContext.CancellationToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
