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
    /// A live process is recognized only when both its identifier and kernel creation identity match the descendant record.
    /// </summary>
    [TestMethod]
    public void DescendantIdentity_RequiresMatchingCreationTime()
    {
        using var process = Process.GetCurrentProcess();
        var prefix = process.Id.ToString(CultureInfo.InvariantCulture) + " ";
        var started = OwnedProcessGroup.GetStartIdentity(process);
        Assert.IsTrue(ComparisonDescendantSource.IsRunning(prefix + started.ToString(CultureInfo.InvariantCulture)));
        Assert.IsFalse(ComparisonDescendantSource.IsRunning(prefix + (started + 1).ToString(CultureInfo.InvariantCulture)));
        Assert.IsNull(ComparisonDescendantSource.Open(prefix + (started + 1).ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// A parent's observation matches the child's published kernel identity while it runs and detects its subsequent exit.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task DescendantIdentity_MatchesAcrossProcessesAndDetectsExit()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var record = Path.Combine(files.DirectoryPath, "processes");
        var ready = Path.Combine(files.DirectoryPath, "ready");
        using var child = ComparisonDescendantSource.Start(Environment.ProcessPath!, record, ready, false, false);
        try
        {
            while (!File.Exists(ready)) await Task.Delay(10, token);
            var identity = Assert.ContainsSingle(await File.ReadAllLinesAsync(record, token));
            var expected = child.Id.ToString(CultureInfo.InvariantCulture) + " "
                + OwnedProcessGroup.GetStartIdentity(child).ToString(CultureInfo.InvariantCulture);
            Assert.AreEqual(expected, identity);
            Assert.IsTrue(ComparisonDescendantSource.IsRunning(identity));
            using var observed = ComparisonDescendantSource.Open(identity);
            Assert.IsNotNull(observed);
            Assert.AreEqual(child.Id, observed.Id);

            child.Kill();
            await OwnedProcessGroup.WaitForExitAsync(child, token);

            Assert.IsFalse(ComparisonDescendantSource.IsRunning(identity));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await OwnedProcessGroup.WaitForExitAsync(child, CancellationToken.None);
        }
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
                TimeoutMilliseconds = mode == "timeout" ? 15_000 : 60_000,
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
    /// Runs a descendant probe when one was requested.
    /// </summary>
    /// <returns>Whether a probe was requested.</returns>
    internal static async Task<bool> TryRunDescendantProbeAsync()
    {
        var record = Environment.GetEnvironmentVariable("ILREPL_DESCENDANT_RECORD");
        if (record is null)
        {
            return false;
        }

        var ready = Environment.GetEnvironmentVariable("ILREPL_DESCENDANT_READY")!;
        var escape = bool.Parse(Environment.GetEnvironmentVariable("ILREPL_DESCENDANT_ESCAPE")!);
        if (escape) ComparisonDescendantSource.EscapeProcessGroup();
        using var process = Process.GetCurrentProcess();
        File.AppendAllText(record, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + " "
            + OwnedProcessGroup.GetStartIdentity(process).ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
        if (bool.Parse(Environment.GetEnvironmentVariable("ILREPL_DESCENDANT_BRANCH")!))
        {
            using var leaf = ComparisonDescendantSource.Start(Environment.ProcessPath!, record, ready, false,
                escape && OperatingSystem.IsWindows());
            while (!File.Exists(ready)) await Task.Delay(10);
            Environment.Exit(0);
        }

        File.WriteAllText(ready, "ready");
        await Task.Delay(Timeout.Infinite);
        return true;
    }
}
