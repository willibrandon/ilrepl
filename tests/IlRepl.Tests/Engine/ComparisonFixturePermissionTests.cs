using System.Globalization;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Actual isolated workers retain captured permission metadata without changing the source fixture tree.
/// </summary>
[TestClass]
public sealed class ComparisonFixturePermissionTests
{
    private const FileAttributes ObservedAttributes = FileAttributes.ReadOnly | FileAttributes.Directory;
    private static readonly string[] LinkPaths = ["alias.txt", "missing.txt", "alias-dir", "missing-dir"];

    /// <summary>
    /// Supplies cancellation for real comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Read-only files, executable bits, directory modes, and root modes stay frozen through repeated isolated execution and cleanup.
    /// </summary>
    [TestMethod]
    public async Task Compare_CapturedPermissionsRemainFrozenAcrossRepeatedWorkers()
    {
        var fixture = CreateFixture();
        try
        {
            var attributes = Attributes(fixture.FullName);
            var modes = Modes(fixture.FullName);
            var expected = new List<string>();
            foreach (var path in ComparisonFixturePermissionExamples.Paths)
            {
                expected.Add(((int)(attributes[path] & ObservedAttributes)).ToString(CultureInfo.InvariantCulture));
                if (modes[path] is { } mode)
                {
                    expected.Add(((int)mode).ToString(CultureInfo.InvariantCulture));
                }
            }

            var session = IlLines.Load(ComparisonFixturePermissionExamples.Source(!OperatingSystem.IsWindows(), true).Split('\n'));
            var edit = session.PrepareEdit("Read", "Copy");
            session.CommitEdit(edit.Name, edit.Source);
            var package = ComparisonCapture.Create(session, "Copy () --files " + LiteralParser.Escape(fixture.FullName));
            Assert.HasCount(ComparisonFixturePermissionExamples.Paths.Count, package.Files);
            foreach (var captured in package.Files)
            {
                Assert.AreEqual(attributes[captured.Path], captured.Attributes, captured.Path);
                Assert.AreEqual(modes[captured.Path], captured.UnixMode, captured.Path);
                Assert.AreEqual(attributes[captured.Path], File.GetAttributes(Path.Join(fixture.FullName, captured.Path)));
                Assert.AreEqual(modes[captured.Path], UnixMode(Path.Join(fixture.FullName, captured.Path)));
            }

            Assert.IsTrue(package.Files.Single(file => file.Path == "readonly.txt").Attributes!.Value.HasFlag(FileAttributes.ReadOnly));
            if (OperatingSystem.IsWindows())
            {
                foreach (var captured in package.Files)
                {
                    Assert.IsNull(captured.UnixMode);
                }
            }
            else
            {
                foreach (var path in ComparisonFixturePermissionExamples.Paths)
                {
                    Assert.AreEqual(ComparisonFixturePermissionExamples.Mode(path),
                        package.Files.Single(file => file.Path == path).UnixMode);
                }
            }

            UnlockFixture(fixture.FullName);
            var changedAttributes = Attributes(fixture.FullName);
            var changedModes = Modes(fixture.FullName);
            Assert.IsFalse(changedAttributes["readonly.txt"].HasFlag(FileAttributes.ReadOnly));
            if (!OperatingSystem.IsWindows())
            {
                Assert.AreNotEqual(modes["."], changedModes["."]);
                Assert.AreNotEqual(modes["nested"], changedModes["nested"]);
                Assert.AreNotEqual(modes["empty"], changedModes["empty"]);
                Assert.AreNotEqual(modes["tool.sh"], changedModes["tool.sh"]);
            }

            for (var run = 0; run < 2; run++)
            {
                var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
                Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
                foreach (var side in new[] { result.Original, result.Edited })
                {
                    Assert.AreEqual("completed", side.Outcome, side.Detail);
                    Assert.IsNull(side.Exception);
                    Assert.AreSequenceEqual(expected, side.Result!.Members.Select(member => member.Value.Value));
                    Assert.HasCount(1, side.Invocations);
                }

                AssertRemovedWorkingDirectory(result);
                AssertSource(fixture.FullName, changedAttributes, changedModes);
            }
        }
        finally
        {
            UnlockFixture(fixture.FullName);
            fixture.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A read-only branch reports the actual edited behavior instead of becoming a false writable-file match.
    /// </summary>
    [TestMethod]
    public async Task Compare_ReadOnlyBranchReportsEditedDifference()
    {
        var fixture = CreateFixture();
        try
        {
            var session = IlLines.Load(ComparisonFixturePermissionExamples.Branch(false).Split('\n'));
            var edit = session.PrepareEdit("Check", "Copy");
            session.CommitEdit(edit.Name, ComparisonFixturePermissionExamples.Branch(true));
            var package = ComparisonCapture.Create(session, "Copy () --files " + LiteralParser.Escape(fixture.FullName));
            Assert.IsTrue(package.Files.Single(file => file.Path == "readonly.txt").Attributes!.Value.HasFlag(FileAttributes.ReadOnly));
            UnlockFixture(fixture.FullName);
            var changedAttributes = Attributes(fixture.FullName);
            var changedModes = Modes(fixture.FullName);
            Assert.IsFalse(changedAttributes["readonly.txt"].HasFlag(FileAttributes.ReadOnly));
            for (var run = 0; run < 2; run++)
            {
                var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
                Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
                Assert.AreEqual("completed", result.Original.Outcome, result.Original.Detail);
                Assert.AreEqual("completed", result.Edited.Outcome, result.Edited.Detail);
                Assert.IsNull(result.Original.Exception);
                Assert.IsNull(result.Edited.Exception);
                Assert.AreEqual("42", result.Original.Result!.Value);
                Assert.AreEqual("43", result.Edited.Result!.Value);
                AssertSource(fixture.FullName, changedAttributes, changedModes);
            }
        }
        finally
        {
            UnlockFixture(fixture.FullName);
            fixture.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Link metadata remains independent of target modes, including dangling internal file and directory links.
    /// </summary>
    [TestMethod]
    public async Task Compare_LinkPermissionsDoNotFollowDanglingTargets()
    {
        var fixture = CreateFixture(links: true);
        try
        {
            var attributes = Attributes(fixture.FullName);
            var modes = Modes(fixture.FullName);
            var session = IlLines.Load(ComparisonFixturePermissionExamples.Links(!OperatingSystem.IsWindows()).Split('\n'));
            var edit = session.PrepareEdit("Read", "Copy");
            session.CommitEdit(edit.Name, edit.Source);
            var package = ComparisonCapture.Create(session, "Copy () --files " + LiteralParser.Escape(fixture.FullName));
            foreach (var path in LinkPaths)
            {
                var captured = package.Files.Single(file => file.Path == path);
                Assert.IsNull(captured.UnixMode, path);
                Assert.IsTrue(captured.Attributes!.Value.HasFlag(FileAttributes.ReparsePoint), path);
                Assert.AreEqual(File.GetAttributes(Path.Join(fixture.FullName, path)), captured.Attributes, path);
            }

            Assert.AreEqual("absent.txt", package.Files.Single(file => file.Path == "missing.txt").LinkTarget);
            Assert.AreEqual("absent-dir", package.Files.Single(file => file.Path == "missing-dir").LinkTarget);
            var expected = new List<string> { "1", "1", "1", "1", "1" };
            if (!OperatingSystem.IsWindows())
            {
                var mode = ComparisonFixturePermissionExamples.Mode("readonly.txt");
                Assert.AreEqual(mode, package.Files.Single(file => file.Path == "readonly.txt").UnixMode);
                Assert.AreEqual(mode, File.GetUnixFileMode(Path.Join(fixture.FullName, "alias.txt")));
                expected.Add(((int)mode).ToString(CultureInfo.InvariantCulture));
            }

            for (var run = 0; run < 2; run++)
            {
                var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
                Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
                foreach (var side in new[] { result.Original, result.Edited })
                {
                    Assert.AreEqual("completed", side.Outcome, side.Detail);
                    Assert.IsNull(side.Exception);
                    Assert.AreSequenceEqual(expected, side.Result!.Members.Select(member => member.Value.Value));
                }

                AssertRemovedWorkingDirectory(result);
                AssertSource(fixture.FullName, attributes, modes);
                Assert.AreEqual("readonly.txt", new FileInfo(Path.Join(fixture.FullName, "alias.txt")).LinkTarget);
                Assert.AreEqual("absent.txt", new FileInfo(Path.Join(fixture.FullName, "missing.txt")).LinkTarget);
                Assert.AreEqual("nested", new DirectoryInfo(Path.Join(fixture.FullName, "alias-dir")).LinkTarget);
                Assert.AreEqual("absent-dir", new DirectoryInfo(Path.Join(fixture.FullName, "missing-dir")).LinkTarget);
            }
        }
        finally
        {
            UnlockFixture(fixture.FullName);
            fixture.Delete(recursive: true);
        }
    }

    private static DirectoryInfo CreateFixture(bool links = false)
    {
        var fixture = Directory.CreateTempSubdirectory("ilrepl-fixture-permissions-");
        Directory.CreateDirectory(Path.Join(fixture.FullName, "nested"));
        Directory.CreateDirectory(Path.Join(fixture.FullName, "empty"));
        File.WriteAllText(Path.Join(fixture.FullName, "nested", "data.txt"), "seed");
        File.WriteAllText(Path.Join(fixture.FullName, "readonly.txt"), "unchanged");
        File.WriteAllText(Path.Join(fixture.FullName, "tool.sh"), "#!/bin/sh\nexit 0\n");
        if (links)
        {
            File.CreateSymbolicLink(Path.Join(fixture.FullName, "alias.txt"), "readonly.txt");
            File.CreateSymbolicLink(Path.Join(fixture.FullName, "missing.txt"), "absent.txt");
            Directory.CreateSymbolicLink(Path.Join(fixture.FullName, "alias-dir"), "nested");
            Directory.CreateSymbolicLink(Path.Join(fixture.FullName, "missing-dir"), "absent-dir");
        }

        foreach (var path in ComparisonFixturePermissionExamples.Paths.Concat(links ? LinkPaths : []).Reverse())
        {
            var fullPath = Path.Join(fixture.FullName, path);
            var attributes = File.GetAttributes(fullPath);
            FileSystemInfo entry = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
            entry.CreationTimeUtc = ComparisonFixtureTimeExamples.Timestamp;
            entry.LastWriteTimeUtc = ComparisonFixtureTimeExamples.Timestamp;
            entry.LastAccessTimeUtc = ComparisonFixtureTimeExamples.Timestamp;
            if (path is "." or "nested" or "empty" or "readonly.txt")
            {
                File.SetAttributes(fullPath, attributes | FileAttributes.ReadOnly);
            }

            if (!OperatingSystem.IsWindows() && !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                File.SetUnixFileMode(fullPath, ComparisonFixturePermissionExamples.Mode(path));
            }
        }

        return fixture;
    }

    private static Dictionary<string, FileAttributes> Attributes(string root) => ComparisonFixturePermissionExamples.Paths
        .ToDictionary(path => path, path => File.GetAttributes(Path.Join(root, path)), StringComparer.Ordinal);

    private static Dictionary<string, UnixFileMode?> Modes(string root) => ComparisonFixturePermissionExamples.Paths
        .ToDictionary(path => path, path => UnixMode(Path.Join(root, path)), StringComparer.Ordinal);

    private static UnixFileMode? UnixMode(string path) => OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);

    private static void AssertSource(
        string root,
        Dictionary<string, FileAttributes> attributes,
        Dictionary<string, UnixFileMode?> modes)
    {
        foreach (var path in ComparisonFixturePermissionExamples.Paths)
        {
            Assert.AreEqual(attributes[path], File.GetAttributes(Path.Join(root, path)), path);
            Assert.AreEqual(modes[path], UnixMode(Path.Join(root, path)), path);
        }

        Assert.AreEqual("seed", File.ReadAllText(Path.Join(root, "nested", "data.txt")));
        Assert.AreEqual("unchanged", File.ReadAllText(Path.Join(root, "readonly.txt")));
        Assert.AreEqual("#!/bin/sh\nexit 0\n", File.ReadAllText(Path.Join(root, "tool.sh")));
    }

    private static void AssertRemovedWorkingDirectory(ComparisonReply result)
    {
        var work = result.Original.StandardOutput.Trim();
        Assert.AreEqual(work, result.Edited.StandardOutput.Trim());
        Assert.IsTrue(Path.IsPathFullyQualified(work), work);
        Assert.AreEqual("work", Path.GetFileName(work));
        var parent = Directory.GetParent(work)!;
        Assert.StartsWith("ilrepl-compare-", parent.Name);
        Assert.IsFalse(Directory.Exists(parent.FullName), work);
    }

    private static void UnlockFixture(string root)
    {
        foreach (var path in ComparisonFixturePermissionExamples.Paths)
        {
            var fullPath = Path.Join(root, path);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                continue;
            }

            var attributes = File.GetAttributes(fullPath);
            File.SetAttributes(fullPath, attributes & ~FileAttributes.ReadOnly);
            if (!OperatingSystem.IsWindows())
            {
                var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    mode |= UnixFileMode.UserExecute;
                }

                File.SetUnixFileMode(fullPath, mode);
            }
        }
    }
}
