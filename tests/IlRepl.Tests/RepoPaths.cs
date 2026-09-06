namespace IlRepl.Tests;

/// <summary>
/// Finds files in the repository from the test output directory.
/// </summary>
internal static class RepoPaths
{
    /// <summary>
    /// The repository root, found by walking up to <c>IlRepl.slnx</c>.
    /// </summary>
    public static string Root { get; } = FindRoot();

    /// <summary>
    /// The build configuration the tests were built with, taken from the output path.
    /// </summary>
    public static string Configuration { get; } =
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";

    /// <summary>
    /// The directory of the transcript samples.
    /// </summary>
    public static string Transcripts => Path.Combine(Root, "samples", "Transcripts");

    /// <summary>
    /// The front-end assembly in the test output.
    /// </summary>
    public static string FrontEndAssembly => Path.Combine(AppContext.BaseDirectory, "ilrepl.dll");

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
