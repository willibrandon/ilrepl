using System.Globalization;
using System.Text;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Observes permission metadata and branches on read-only files inside actual comparison working directories.
/// </summary>
public static class ComparisonFixturePermissionExamples
{
    /// <summary>
    /// The fixture root, directories, ordinary file, read-only file, and executable whose permissions are observed.
    /// </summary>
    public static IReadOnlyList<string> Paths { get; } = [".", "nested", "nested/data.txt", "empty", "readonly.txt", "tool.sh"];

    /// <summary>
    /// Supplies distinct executable, file, and restricted directory modes without making source entries unreadable.
    /// </summary>
    /// <param name="path">The relative fixture entry.</param>
    /// <returns>The exact Unix permission bits captured for the entry.</returns>
    public static UnixFileMode Mode(string path) => path switch
    {
        "." or "nested" => UnixFileMode.UserRead | UnixFileMode.UserExecute,
        "empty" => UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupExecute,
        "readonly.txt" => UnixFileMode.UserRead | UnixFileMode.GroupRead,
        "tool.sh" => UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
        _ => UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
    };

    /// <summary>
    /// Reads meaningful attributes and optional Unix modes before leaving a restrictive tree for real worker cleanup.
    /// </summary>
    /// <param name="unix">Whether the runtime supports Unix mode APIs.</param>
    /// <param name="restrictAfterRead">Whether to remove directory access after collecting the observation.</param>
    /// <returns>The complete method declaration.</returns>
    public static string Source(bool unix, bool restrictAfterRead)
    {
        var source = new StringBuilder(".method public static int32[] Read() {\n"
            + "call string Environment::get_CurrentDirectory()\ncall void Console::WriteLine(string)\nldc.i4 ");
        source.Append((Paths.Count * (unix ? 2 : 1)).ToString(CultureInfo.InvariantCulture)).Append("\nnewarr int32\n");
        var index = 0;
        foreach (var path in Paths)
        {
            source.Append("dup\nldc.i4 ").Append((index++).ToString(CultureInfo.InvariantCulture))
                .Append("\nldstr \"").Append(path).Append("\"\ncall valuetype FileAttributes File::GetAttributes(string)\n")
                .Append("ldc.i4.s 17\nand\nstelem.i4\n");
            if (unix)
            {
                source.Append("dup\nldc.i4 ").Append((index++).ToString(CultureInfo.InvariantCulture))
                    .Append("\nldstr \"").Append(path).Append("\"\ncall valuetype UnixFileMode File::GetUnixFileMode(string)\nstelem.i4\n");
            }
        }

        if (restrictAfterRead)
        {
            source.Append("ldstr \"nested/data.txt\"\nldc.i4.1\ncall void File::SetAttributes(string, valuetype FileAttributes)\n");
            if (unix)
            {
                foreach (var path in new[] { "nested", "." })
                {
                    source.Append("ldstr \"").Append(path)
                        .Append("\"\nldc.i4.0\ncall void File::SetUnixFileMode(string, valuetype UnixFileMode)\n");
                }
            }
        }

        return source.Append("ret\n}").ToString();
    }

    /// <summary>
    /// Reads valid and dangling internal link targets without following absent entries during fixture preparation.
    /// </summary>
    /// <param name="unix">Whether to observe the valid alias through the Unix mode getter.</param>
    /// <returns>The complete method declaration.</returns>
    public static string Links(bool unix)
    {
        var source = new StringBuilder(".method public static int32[] Read() {\n"
            + "call string Environment::get_CurrentDirectory()\ncall void Console::WriteLine(string)\nldc.i4 ")
            .Append(unix ? "6" : "5").Append("\nnewarr int32\n");
        var links = new[] { ("alias.txt", "readonly.txt"), ("missing.txt", "absent.txt"),
            ("alias-dir", "nested"), ("missing-dir", "absent-dir") };
        for (var index = 0; index < links.Length; index++)
        {
            var (path, target) = links[index];
            source.Append("dup\nldc.i4 ").Append(index.ToString(CultureInfo.InvariantCulture))
                .Append("\nldstr \"" + path + "\"\nnewobj instance void ")
                .Append(path.EndsWith("dir", StringComparison.Ordinal) ? "DirectoryInfo" : "FileInfo")
                .Append("::.ctor(string)\ncallvirt instance string FileSystemInfo::get_LinkTarget()\nldstr \"")
                .Append(target).Append("\"\ncall bool String::op_Equality(string, string)\nstelem.i4\n");
        }

        source.Append("dup\nldc.i4.4\nldstr \"readonly.txt\"\ncall valuetype FileAttributes File::GetAttributes(string)\n")
            .Append("ldc.i4.1\nand\nstelem.i4\n");
        if (unix)
        {
            source.Append("dup\nldc.i4.5\nldstr \"alias.txt\"\ncall valuetype UnixFileMode File::GetUnixFileMode(string)\nstelem.i4\n");
        }

        return source.Append("ret\n}").ToString();
    }

    /// <summary>
    /// Makes permission loss observably collapse an intended comparison difference into a false match.
    /// </summary>
    /// <param name="edited">Whether the read-only branch returns the edited value.</param>
    /// <returns>The complete permission-dependent method declaration.</returns>
    public static string Branch(bool edited) => ".method public static int32 Check() {\n"
        + "ldstr \"readonly.txt\"\ncall valuetype FileAttributes File::GetAttributes(string)\nldc.i4.1\nand\nbrfalse Writable\n"
        + (edited ? "ldc.i4.s 43\n" : "ldc.i4.s 42\n") + "ret\nWritable: ldc.i4.0\nret\n}";
}
