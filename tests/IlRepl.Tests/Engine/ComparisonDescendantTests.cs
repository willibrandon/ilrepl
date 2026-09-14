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
    /// Background descendants cannot survive worker completion, failure, cancellation, or timeout.
    /// </summary>
    /// <param name="grandchild">Whether the leaf outlives an intermediate parent as well as the comparison worker.</param>
    /// <param name="mode">The way the compared method ends.</param>
    [TestMethod]
    [DataRow(false, "return")]
    [DataRow(true, "return")]
    [DataRow(false, "exit")]
    [DataRow(true, "exit")]
    [DataRow(false, "throw")]
    [DataRow(true, "throw")]
    [DataRow(false, "timeout")]
    [DataRow(true, "timeout")]
    [DataRow(false, "cancel")]
    [DataRow(true, "cancel")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Compare_StopsDescendantsAfterWorkerExit(bool grandchild, string mode)
    {
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
                "call int32 [IlRepl.Tests]IlRepl.Tests.Engine.ComparisonDescendantSource::Run(string, string, bool, string)", "ret", "}"))
            {
                session.AddLine(line);
            }

            var edit = session.PrepareEdit("Work", "Copy");
            session.CommitEdit(edit.Name, edit.Source);
            var package = ComparisonCapture.Create(session, "Copy ()") with { TimeoutMilliseconds = 15_000 };
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

            var pids = File.ReadAllLines(record).Select(line => int.Parse(line, CultureInfo.InvariantCulture)).ToArray();
            Assert.HasCount((mode == "cancel" ? 1 : 2) * (grandchild ? 2 : 1), pids);
            foreach (var pid in pids)
            {
                Assert.IsFalse(ComparisonDescendantSource.IsRunning(pid), $"descendant {pid} survived comparison cleanup");
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
                        using var process = Process.GetProcessById(int.Parse(line, CultureInfo.InvariantCulture));
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
        File.AppendAllText(record!, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
        if (bool.Parse(Environment.GetEnvironmentVariable("ILREPL_DESCENDANT_BRANCH")!))
        {
            using var leaf = ComparisonDescendantSource.Start(Environment.ProcessPath!, record!, ready, false);
            while (!File.Exists(ready)) await Task.Delay(10, TestContext.CancellationToken);
            Environment.Exit(0);
        }

        File.WriteAllText(ready, "ready");
        await Task.Delay(Timeout.Infinite, TestContext.CancellationToken);
    }
}
