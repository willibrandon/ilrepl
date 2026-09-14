using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Restores fixture metadata after the complete directory tree has been materialized.
/// </summary>
public static partial class ComparisonWorker
{
    private static void RestoreFixtureTimes(IReadOnlyList<ComparisonFile> files, Action<ComparisonFile>? restore)
    {
        foreach (var file in files.OrderByDescending(file => file.Path.Count(character => character == '/'))
            .ThenBy(file => file.IsDirectory).ThenBy(file => file.Path == "."))
        {
            FileSystemInfo entry = file.IsDirectory ? new DirectoryInfo(file.Path) : new FileInfo(file.Path);
            if (restore is not null)
            {
                restore(file);
            }
            else
            {
                if (file.CreationTimeUtc is { } creation)
                {
                    entry.CreationTimeUtc = creation;
                }

                if (file.LastWriteTimeUtc is { } written)
                {
                    entry.LastWriteTimeUtc = written;
                }

                if (file.LastAccessTimeUtc is { } accessed)
                {
                    entry.LastAccessTimeUtc = accessed;
                }
            }

            entry.Refresh();
            Verify(file.CreationTimeUtc, entry.CreationTimeUtc, "creation");
            Verify(file.LastWriteTimeUtc, entry.LastWriteTimeUtc, "last-write");
            Verify(file.LastAccessTimeUtc, entry.LastAccessTimeUtc, "last-access");
            // Inspecting a symbolic link's metadata can update its access time.
            if (restore is null && file.LastAccessTimeUtc is { } lastAccess)
            {
                entry.LastAccessTimeUtc = lastAccess;
            }

            void Verify(DateTime? expected, DateTime actual, string name)
            {
                if (expected is { } time && time != actual)
                {
                    throw new ReplException($"cannot restore {name} time for comparison fixture '{file.Path}' on this filesystem");
                }
            }
        }
    }
}
