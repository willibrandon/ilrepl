using System.Globalization;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Isolated workers retain captured fixture timestamps independently of later changes to the source tree.
/// </summary>
[TestClass]
public sealed class ComparisonFixtureTimestampTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Files, directory roots, nested directories, and links retain all captured timestamps in both workers.
    /// </summary>
    [TestMethod]
    public async Task Compare_RestoresCapturedTimestampsAfterMaterializingTheTree()
    {
        var fixture = Directory.CreateTempSubdirectory("ilrepl-fixture-times-");
        try
        {
            Directory.CreateDirectory(Path.Join(fixture.FullName, "nested"));
            Directory.CreateDirectory(Path.Join(fixture.FullName, "empty"));
            File.WriteAllText(Path.Join(fixture.FullName, "nested", "data.txt"), "seed");
            File.CreateSymbolicLink(Path.Join(fixture.FullName, "alias.txt"), "nested/data.txt");
            Directory.CreateSymbolicLink(Path.Join(fixture.FullName, "alias-dir"), "empty");
            var entries = ComparisonFixtureTimeExamples.Paths.Select(path => Entry(Path.Join(fixture.FullName, path))).ToArray();
            foreach (var entry in entries.Reverse())
            {
                entry.CreationTimeUtc = ComparisonFixtureTimeExamples.Timestamp.AddDays(-20);
                entry.LastWriteTimeUtc = ComparisonFixtureTimeExamples.Timestamp.AddDays(-10);
                entry.LastAccessTimeUtc = ComparisonFixtureTimeExamples.Timestamp;
                entry.Refresh();
            }

            var expected = entries.SelectMany(entry => new[] { entry.CreationTimeUtc, entry.LastWriteTimeUtc, entry.LastAccessTimeUtc })
                .Select(time => time.Ticks.ToString(CultureInfo.InvariantCulture)).ToArray();
            foreach (var entry in entries)
            {
                entry.LastAccessTimeUtc = ComparisonFixtureTimeExamples.Timestamp;
            }

            var session = IlLines.Load(ComparisonFixtureTimeExamples.Source().Split('\n'));
            var edit = session.PrepareEdit("Read", "Copy");
            session.CommitEdit(edit.Name, edit.Source);
            var package = ComparisonCapture.Create(session, "Copy () --files " + LiteralParser.Escape(fixture.FullName));
            foreach (var (path, index) in ComparisonFixtureTimeExamples.Paths.Select((path, index) => (path, index)))
            {
                var captured = package.Files.Single(file => file.Path == path);
                Assert.AreEqual(expected[index * 3], captured.CreationTimeUtc!.Value.Ticks.ToString(CultureInfo.InvariantCulture));
                Assert.AreEqual(expected[(index * 3) + 1], captured.LastWriteTimeUtc!.Value.Ticks.ToString(CultureInfo.InvariantCulture));
                Assert.AreEqual(expected[(index * 3) + 2],
                    captured.LastAccessTimeUtc!.Value.Ticks.ToString(CultureInfo.InvariantCulture), path);
            }

            var changed = ComparisonFixtureTimeExamples.Timestamp.AddYears(10);
            foreach (var entry in entries)
            {
                entry.LastWriteTimeUtc = changed;
            }

            var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
            Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
            foreach (var side in new[] { result.Original, result.Edited })
            {
                Assert.AreEqual("completed", side.Outcome);
                Assert.AreSequenceEqual(expected, side.Result!.Members.Select(member => member.Value.Value));
            }

            foreach (var entry in entries)
            {
                entry.Refresh();
                Assert.AreEqual(changed, entry.LastWriteTimeUtc);
            }
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }

    private static FileSystemInfo Entry(string path) => File.GetAttributes(path).HasFlag(FileAttributes.Directory)
        ? new DirectoryInfo(path) : new FileInfo(path);
}
