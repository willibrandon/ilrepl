namespace IlRepl.Host;

/// <summary>
/// Owns private worker directories and removes their files without following symbolic links.
/// </summary>
internal static class ComparisonDirectory
{
    /// <summary>
    /// Creates a worker's control directory before captured inputs and environment values are written.
    /// </summary>
    /// <param name="path">The unique worker-owned temporary directory.</param>
    internal static void Create(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Deletes a comparison's temporary tree so the next worker starts with fresh files at the same path.
    /// </summary>
    /// <param name="path">The temporary path owned by the comparison.</param>
    internal static void Delete(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        var directory = attributes.HasFlag(FileAttributes.Directory);
        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (OperatingSystem.IsWindows())
            {
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }
            else if (directory)
            {
                File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                    | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            if (directory)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                {
                    Delete(entry);
                }
            }
        }

        if (directory)
        {
            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }
    }
}
