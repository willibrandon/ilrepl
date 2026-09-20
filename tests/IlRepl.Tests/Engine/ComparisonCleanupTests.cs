using System.Runtime.Versioning;
using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Filesystem cleanup failures cannot replace observations from real comparison workers.
/// </summary>
[TestClass]
public sealed class ComparisonCleanupTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Resetting each worker removes symbolic links without changing permissions or files outside its temporary tree.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Run_LinkedDirectory_DoesNotChangeTheTarget()
    {
        var outside = Directory.CreateTempSubdirectory("ilrepl-cleanup-outside-");
        var file = Path.Join(outside.FullName, "data.txt");
        File.WriteAllText(file, "outside");
        File.SetUnixFileMode(outside.FullName, UnixFileMode.None);
        try
        {
            var session = new Session();
            session.Resolver.Load(typeof(ComparisonCleanupSource).Assembly.Location);
            foreach (var line in IlLines.Expand(".method string Work() {", "ldstr " + LiteralParser.Escape(outside.FullName),
                "call string [IlRepl.Tests]IlRepl.Tests.Engine.ComparisonCleanupSource::Link(string)", "ret", "}"))
            {
                session.AddLine(line);
            }

            var edit = session.PrepareEdit("Work", "Copy");
            session.CommitEdit(edit.Name, edit.Source);
            var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
                TestContext.CancellationToken);
            Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
            Assert.AreEqual(UnixFileMode.None, File.GetUnixFileMode(outside.FullName));
            Assert.IsFalse(Directory.Exists(result.Original.Result!.Value));
            File.SetUnixFileMode(outside.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.AreEqual("outside", File.ReadAllText(file));
        }
        finally
        {
            File.SetUnixFileMode(outside.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            outside.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Both completed and crashed workers retain their outcomes when their files cannot be deleted.
    /// </summary>
    /// <param name="crash">Whether both workers exit without producing an observation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_RestrictedWorkingDirectory_PreservesTheComparisonOutcome(bool crash)
    {
        var records = Directory.CreateTempSubdirectory("ilrepl-cleanup-records-");
        var record = Path.Join(records.FullName, "workers.txt");
        try
        {
            var session = new Session();
            session.Resolver.Load(typeof(ComparisonCleanupSource).Assembly.Location);
            foreach (var line in IlLines.Expand(".method int32 Work() {", "ldstr " + LiteralParser.Escape(record),
                crash ? "ldc.i4.1" : "ldc.i4.0",
                "call int32 [IlRepl.Tests]IlRepl.Tests.Engine.ComparisonCleanupSource::Run(string, bool)", "ret", "}"))
            {
                session.AddLine(line);
            }

            var edit = session.PrepareEdit("Work", "Copy");
            session.CommitEdit(edit.Name, edit.Source);

            var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
                TestContext.CancellationToken);

            Assert.AreEqual(crash ? "incomplete" : "match", result.Outcome);
            foreach (var side in new[] { result.Original, result.Edited })
            {
                Assert.AreEqual(crash ? "crashed" : "completed", side.Outcome, side.Detail);
                if (crash)
                {
                    Assert.AreEqual("comparison host exited with code 23", side.Detail);
                    Assert.IsEmpty(side.Invocations);
                }
                else
                {
                    Assert.IsNull(side.Exception);
                    Assert.AreEqual("42", side.Result!.Value);
                    Assert.HasCount(1, side.Invocations);
                    Assert.AreEqual("42", side.Invocations[0].Outputs.Single(member => member.Name == "return").Value.Value);
                }
            }

            var workers = File.ReadAllLines(record);
            Assert.HasCount(2, workers);
            Assert.AreEqual(workers[0], workers[1]);
            Assert.IsFalse(Directory.Exists(Directory.GetParent(workers[0])!.FullName));
        }
        finally
        {
            if (File.Exists(record))
            {
                foreach (var work in File.ReadAllLines(record))
                {
                    RestoreAndDelete(work);
                }
            }

            records.Delete(recursive: true);
        }
    }

    private static void RestoreAndDelete(string work)
    {
        var locked = Path.Join(work, "locked");
        if (Directory.Exists(locked))
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(Path.Join(locked, "data.txt"), FileAttributes.Normal);
            }
            else
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        var root = Directory.GetParent(work)!;
        if (root.Exists)
        {
            root.Delete(recursive: true);
        }
    }
}
