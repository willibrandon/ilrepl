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
    /// Both completed and crashed workers retain their outcomes when their files cannot be deleted.
    /// </summary>
    /// <param name="crash">Whether both workers exit without producing an observation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_RestrictedWorkingDirectory_PreservesTheComparisonOutcome(bool crash)
    {
        var records = Directory.CreateTempSubdirectory("ilrepl-cleanup-records-");
        var record = Path.Combine(records.FullName, "workers.txt");
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
            Assert.AreNotEqual(workers[0], workers[1]);
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
        var locked = Path.Combine(work, "locked");
        if (Directory.Exists(locked))
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(Path.Combine(locked, "data.txt"), FileAttributes.Normal);
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
