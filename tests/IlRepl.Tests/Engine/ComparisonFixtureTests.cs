using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Real comparison workers receive complete independent fixture trees, including empty directories and symbolic links.
/// </summary>
[TestClass]
public sealed class ComparisonFixtureTests
{
    /// <summary>
    /// The cancellation context for real worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Both workers read identical linked files and empty directories, and mutations leave the supplied fixtures unchanged.
    /// </summary>
    /// <returns>The completed filesystem and worker assertions.</returns>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Compare_CopiesEmptyDirectoriesAndInternalLinks()
    {
        var fixture = Directory.CreateTempSubdirectory("ilrepl-fixture-tree-");
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture.FullName, "empty"));
            File.WriteAllText(Path.Combine(fixture.FullName, "data.txt"), "seed");
            File.CreateSymbolicLink(Path.Combine(fixture.FullName, "alias.txt"), "data.txt");
            Directory.CreateSymbolicLink(Path.Combine(fixture.FullName, "alias-dir"), "empty");
            Directory.CreateSymbolicLink(Path.Combine(fixture.FullName, "alias-root"), ".");
            var session = IlLines.Load(".method bool Read() {",
                "ldstr \"alias-root/data.txt\"", "call string File::ReadAllText(string)", "call void Console::Write(string)",
                "ldstr \"alias.txt\"", "ldstr \"worker\"", "call void File::WriteAllText(string, string)",
                "ldstr \"alias-dir\"", "call bool Directory::Exists(string)", "ret", "}");
            var edit = session.PrepareEdit("Read", "Copy");
            session.CommitEdit(edit.Name, edit.Source);
            var package = ComparisonCapture.Create(session, "Copy () --files " + LiteralParser.Escape(fixture.FullName));

            Assert.HasCount(5, package.Files);
            Assert.HasCount(3, package.Files.Where(file => file.IsDirectory));
            Assert.HasCount(3, package.Files.Where(file => file.LinkTarget is not null));
            var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

            Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
            foreach (var side in new[] { result.Original, result.Edited })
            {
                Assert.AreEqual("completed", side.Outcome);
                Assert.AreEqual("seed", side.StandardOutput);
                Assert.AreEqual("true", side.Result!.Value);
                Assert.HasCount(1, side.Invocations);
            }

            Assert.AreEqual("seed", File.ReadAllText(Path.Combine(fixture.FullName, "data.txt")));
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.FullName, "empty")));
            Assert.AreEqual("data.txt", new FileInfo(Path.Combine(fixture.FullName, "alias.txt")).LinkTarget);
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A fixture link to shared external state is rejected before either worker is prepared.
    /// </summary>
    [TestMethod]
    public void Capture_ExternalFixtureLinkReportsItsPath()
    {
        var fixture = Directory.CreateTempSubdirectory("ilrepl-fixture-external-");
        try
        {
            var link = Path.Combine(fixture.FullName, "shared");
            Directory.CreateSymbolicLink(link, fixture.Parent!.FullName);
            var session = IlLines.Load(".method int32 Value() { ldc.i4.s 42; ret }");
            var edit = session.PrepareEdit("Value", "Copy");
            session.CommitEdit(edit.Name, edit.Source);

            var error = Assert.ThrowsExactly<ReplException>(() => ComparisonCapture.Create(session,
                "Copy () --files " + LiteralParser.Escape(fixture.FullName)));

            Assert.Contains(link, error.Message);
            Assert.Contains("outside the captured directory", error.Message);
            Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }
}
