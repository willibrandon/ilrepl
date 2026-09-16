namespace IlRepl.Tests;

/// <summary>
/// Replaces loaded fixture images by renaming them and removes their files after collectible contexts have been released.
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

    /// <summary>
    /// Deletes an owned fixture directory after the test method has returned and released its reflection references.
    /// </summary>
    /// <param name="path">The fixture directory owned by the completed test.</param>
    internal static void DeleteDirectory(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (OperatingSystem.IsWindows() && attempt < 10
                && exception is IOException or UnauthorizedAccessException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }
}
