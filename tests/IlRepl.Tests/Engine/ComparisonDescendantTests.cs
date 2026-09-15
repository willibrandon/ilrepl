using System.Diagnostics;
using System.Globalization;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Comparison cleanup terminates real child and grandchild processes before the next side reuses its working directory.
/// </summary>
[TestClass]
public sealed class ComparisonDescendantTests
{
    /// <summary>
    /// Supplies cancellation for the comparison and descendant probes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A live process is recognized only when both its identifier and creation time match the descendant record.
    /// </summary>
    [TestMethod]
    public void DescendantIdentity_RequiresMatchingCreationTime()
    {
        using var process = Process.GetCurrentProcess();
        var prefix = process.Id.ToString(CultureInfo.InvariantCulture) + " ";
        var started = process.StartTime.ToUniversalTime().Ticks;
        Assert.IsTrue(ComparisonDescendantSource.IsRunning(prefix + started.ToString(CultureInfo.InvariantCulture)));
        Assert.IsFalse(ComparisonDescendantSource.IsRunning(prefix + (started + 1).ToString(CultureInfo.InvariantCulture)));
        Assert.IsNull(ComparisonDescendantSource.Open(prefix + (started + 1).ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Background descendants cannot survive worker completion, failure, cancellation, or timeout.
    /// </summary>
    /// <param name="grandchild">Whether the leaf outlives an intermediate parent as well as the comparison worker.</param>
    /// <param name="mode">The way the compared method ends.</param>
    /// <param name="escape">Whether the child leaves the worker's Unix process group.</param>
    [TestMethod]
    [DataRow(false, "return", false)]
    [DataRow(true, "return", false)]
    [DataRow(false, "exit", false)]
    [DataRow(true, "exit", false)]
    [DataRow(false, "throw", false)]
    [DataRow(true, "throw", false)]
    [DataRow(false, "timeout", false)]
    [DataRow(true, "timeout", false)]
    [DataRow(false, "cancel", false)]
    [DataRow(true, "cancel", false)]
    [DataRow(false, "return", true)]
    [DataRow(false, "throw", true)]
    [DataRow(false, "timeout", true)]
    [DataRow(false, "cancel", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Compare_StopsDescendantsAfterWorkerExit(bool grandchild, string mode, bool escape)
    {
        TestSkip.Unless(!escape || !OperatingSystem.IsWindows(), "Unix session escape is not available on Windows");
        var records = Directory.CreateTempSubdirectory("ilrepl-descendants-");
        var record = Path.Combine(records.FullName, "processes");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        Task<ComparisonReply>? running = null;
        try
        {
            var session = new Session();
            session.Resolver.Load(typeof(ComparisonDescendantSource).Assembly.Location);
            foreach (var line in IlLines.Expand(".method int32 Work() {", "ldstr " + LiteralParser.Escape(Environment.ProcessPath!),
                "ldstr " + LiteralParser.Escape(records.FullName), grandchild ? "ldc.i4.1" : "ldc.i4.0",
                "ldstr " + LiteralParser.Escape(mode),
                escape ? "ldc.i4.1" : "ldc.i4.0",
                "call int32 [IlRepl.Tests]IlRepl.Tests.Engine.ComparisonDescendantSource::Run(string, string, bool, string, bool)",
                "ret", "}"))
            {
                session.AddLine(line);
            }

            var edit = session.PrepareEdit("Work", "Copy");
            session.CommitEdit(edit.Name, edit.Source);
            var package = ComparisonCapture.Create(session, "Copy ()") with
            {
                TimeoutMilliseconds = mode == "timeout" ? 1_000 : 60_000,
            };
            running = ProcessComparisonRunner.RunAsync(package, cancel.Token);
            if (mode == "cancel")
            {
                while (!Directory.EnumerateFiles(records.FullName).Any(path => path != record))
                {
                    await Task.Delay(10, TestContext.CancellationToken);
                }
                await cancel.CancelAsync();
            }

            var result = await running;
            var expected = mode switch { "exit" => "crashed", "timeout" => "timeout", "cancel" => "cancelled", _ => "completed" };
            foreach (var side in new[] { result.Original, result.Edited })
            {
                Assert.AreEqual(expected, side.Outcome, side.Detail);
                if (mode == "return") Assert.AreEqual("42", side.Result!.Value, "an earlier descendant contaminated the edited side");
                if (mode == "throw") Assert.AreEqual("worker failure", side.Invocations.Single().Exception!.Message);
            }

            var descendants = File.ReadAllLines(record);
            Assert.HasCount((mode == "cancel" ? 1 : 2) * (grandchild ? 2 : 1), descendants);
            foreach (var descendant in descendants)
            {
                Assert.IsFalse(ComparisonDescendantSource.IsRunning(descendant), $"descendant {descendant} survived comparison cleanup");
            }
        }
        finally
        {
            await cancel.CancelAsync();
            if (running is not null) await running;
            if (File.Exists(record))
            {
                foreach (var line in File.ReadAllLines(record))
                {
                    try
                    {
                        using var process = ComparisonDescendantSource.Open(line);
                        if (process is null) continue;
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                    catch (ArgumentException)
                    {
                        // Cleanup already removed the process.
                    }
                }
            }

            records.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Acts as an inherited-stream child or grandchild until the comparison's native process group terminates it.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task RunDescendantProbe()
    {
        var record = Environment.GetEnvironmentVariable("ILREPL_DESCENDANT_RECORD");
        TestSkip.Unless(record is not null, "runs as a child of Compare_StopsDescendantsAfterWorkerExit");
        var ready = Environment.GetEnvironmentVariable("ILREPL_DESCENDANT_READY")!;
        if (bool.Parse(Environment.GetEnvironmentVariable("ILREPL_DESCENDANT_ESCAPE")!))
            ComparisonDescendantSource.EscapeProcessGroup();
        using var process = Process.GetCurrentProcess();
        File.AppendAllText(record!, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + " "
            + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
        if (bool.Parse(Environment.GetEnvironmentVariable("ILREPL_DESCENDANT_BRANCH")!))
        {
            using var leaf = ComparisonDescendantSource.Start(Environment.ProcessPath!, record!, ready, false, false);
            while (!File.Exists(ready)) await Task.Delay(10, TestContext.CancellationToken);
            Environment.Exit(0);
        }

        File.WriteAllText(ready, "ready");
        await Task.Delay(Timeout.Infinite, TestContext.CancellationToken);
    }
}
