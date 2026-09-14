using System.Globalization;
using System.Text;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Reads fixture timestamps through the filesystem APIs used by ordinary .NET programs.
/// </summary>
public static class ComparisonFixtureTimeExamples
{
    /// <summary>
    /// The root, files, directories, and symbolic links whose timestamps are observed.
    /// </summary>
    public static IReadOnlyList<string> Paths { get; } = [".", "nested", "nested/data.txt", "empty", "alias.txt", "alias-dir"];

    /// <summary>
    /// A past timestamp with precision supported by desktop and browser filesystems.
    /// </summary>
    public static DateTime Timestamp { get; } = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    /// <summary>
    /// Returns creation, last-write, and last-access ticks for every fixture entry without changing its contents.
    /// </summary>
    /// <returns>The complete method declaration.</returns>
    public static string Source()
    {
        var source = new StringBuilder(".method public static int64[] Read() {\n"
            + ".locals init (valuetype DateTime stamp, class FileInfo entry)\nldc.i4 ");
        source.Append((Paths.Count * 3).ToString(CultureInfo.InvariantCulture)).Append("\nnewarr int64\n");
        var index = 0;
        foreach (var path in Paths)
        {
            source.Append("ldstr \"").Append(path).Append("\"\nnewobj instance void FileInfo::.ctor(string)\nstloc.1\n");
            foreach (var getter in new[] { "get_CreationTimeUtc", "get_LastWriteTimeUtc", "get_LastAccessTimeUtc" })
            {
                source.Append("dup\nldc.i4 ").Append((index++).ToString(CultureInfo.InvariantCulture))
                    .Append("\nldloc.1\ncallvirt instance valuetype DateTime FileSystemInfo::").Append(getter)
                    .Append("()\nstloc.0\nldloca.s 0\ncall instance int64 DateTime::get_Ticks()\nstelem.i8\n");
            }
        }

        return source.Append("ret\n}").ToString();
    }
}
