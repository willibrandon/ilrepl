namespace IlRepl.Docs.Tests;

/// <summary>
/// Finds the built docs site from the test output directory.
/// </summary>
internal static class SitePaths
{
    /// <summary>
    /// The repository root, found by walking up to <c>IlRepl.slnx</c>.
    /// </summary>
    public static string Root { get; } = FindRoot();

    /// <summary>
    /// The Astro build output.
    /// </summary>
    public static string Dist => Path.Combine(Root, "docs", "dist");

    private static string FindRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "IlRepl.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("could not find the repository root (IlRepl.slnx)");
    }
}
