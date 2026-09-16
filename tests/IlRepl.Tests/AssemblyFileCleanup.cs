namespace IlRepl.Tests;

/// <summary>
/// Replaces loaded fixture images by renaming them while their original bytes remain mapped by the runtime.
/// </summary>
internal static class AssemblyFileCleanup
{
    /// <summary>
    /// Keeps a mapped image intact while publishing rebuilt bytes at the original path on every supported operating system.
    /// </summary>
    /// <param name="path">The existing loaded image.</param>
    /// <param name="image">The rebuilt assembly bytes.</param>
    internal static void Replace(string path, byte[] image)
    {
        File.Move(path, path + "." + Guid.NewGuid().ToString("N") + ".previous");
        File.WriteAllBytes(path, image);
    }
}
