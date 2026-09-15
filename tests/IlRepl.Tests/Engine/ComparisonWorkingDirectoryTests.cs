using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Both process comparison sides observe one working path while receiving independent fixture state.
/// </summary>
[TestClass]
public sealed class ComparisonWorkingDirectoryTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Directory APIs and absolute fixture paths agree across fresh workers, even when the first worker changes its files.
    /// </summary>
    /// <param name="kind">The directory or absolute-path API observed by the selected method.</param>
    [TestMethod]
    [DataRow("directory")]
    [DataRow("environment")]
    [DataRow("path")]
    [DataRow("file")]
    public async Task Compare_UsesTheSamePathWithFreshFiles(string kind)
    {
        var fixture = Directory.CreateTempSubdirectory("ilrepl-path-fixture-");
        try
        {
            File.WriteAllText(Path.Combine(fixture.FullName, "data.txt"), "seed");
            var package = Capture(kind, fixture.FullName);
            var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
            Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
            foreach (var side in new[] { result.Original, result.Edited })
            {
                Assert.AreEqual("seed", side.StandardOutput);
                var path = side.Result!.Value!;
                Assert.IsTrue(Path.IsPathFullyQualified(path));
                var work = kind is "path" or "file" ? Path.GetDirectoryName(path)! : path;
                Assert.IsFalse(Directory.Exists(work));
                Assert.AreNotEqual(fixture.FullName, work);
            }

            Assert.AreEqual("seed", File.ReadAllText(Path.Combine(fixture.FullName, "data.txt")));
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Concurrent comparisons share no working path or fixture mutations with one another.
    /// </summary>
    [TestMethod]
    public async Task Compare_ConcurrentRunsKeepDistinctDirectories()
    {
        var fixture = Directory.CreateTempSubdirectory("ilrepl-parallel-path-");
        try
        {
            File.WriteAllText(Path.Combine(fixture.FullName, "data.txt"), "seed");
            var package = Capture("directory", fixture.FullName);
            var results = await Task.WhenAll(ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken),
                ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken));
            foreach (var result in results)
            {
                Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
                Assert.AreEqual("seed", result.Original.StandardOutput);
                Assert.AreEqual("seed", result.Edited.StandardOutput);
            }

            Assert.AreNotEqual(results[0].Original.Result!.Value, results[1].Original.Result!.Value);
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }

    private static ComparisonPackage Capture(string kind, string fixture)
    {
        var source = kind switch
        {
            "environment" => "call string Environment::get_CurrentDirectory()",
            "path" => "ldstr \"data.txt\"\ncall string Path::GetFullPath(string)",
            "file" => "ldstr \"data.txt\"\nnewobj instance void FileInfo::.ctor(string)\n"
                + "callvirt instance string FileSystemInfo::get_FullName()",
            _ => "call string Directory::GetCurrentDirectory()",
        };
        var session = IlLines.Load((".method string Location() {\n" + source + "\n"
            + "ldstr \"data.txt\"\ncall string File::ReadAllText(string)\ncall void Console::Write(string)\n"
            + "ldstr \"data.txt\"\nldstr \"changed\"\ncall void File::WriteAllText(string, string)\nret\n}").Split('\n'));
        var edit = session.PrepareEdit("Location", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        return ComparisonCapture.Create(session, "Copy () --files " + LiteralParser.Escape(fixture));
    }
}
