using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies explicit workloads, captured input, cancellation, and process output limits in actual CoreCLR workers.
/// </summary>
[TestClass]
public sealed class NativeWorkloadProcessTests
{
    /// <summary>
    /// Supplies cancellation to actual worker lifecycles.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A named scenario supplies the selected method's arguments and both identities remain visible in the report.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_ScenarioInvokesSelectedMethodWithExplicitArguments()
    {
        using var core = new ReplCore();
        Submit(core, ".method void Step(int32 value) { ldarg.0; call void Console::Write(int32); ret }",
            ".method void Scenario() { ldc.i4.s 42; call Step; ret }");
        var package = Prepare(core, ".jit Step using Scenario");

        var result = await ProcessNativeRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("complete", result.Outcome, Details(result));
        Assert.AreEqual(1, result.Left.Invocations);
        Assert.AreEqual("42", result.Left.StandardOutput);
        Assert.IsEmpty(result.Left.StandardError);
        Assert.Contains("Step", Assert.ContainsSingle(result.Left.Compilations).Method);
        Assert.Contains(role => role.StartsWith("implementation:", StringComparison.Ordinal)
            && role.Contains("Step", StringComparison.Ordinal), result.Left.Roles);
        Assert.Contains(role => role.StartsWith("invocation:", StringComparison.Ordinal)
            && role.Contains("Scenario", StringComparison.Ordinal), result.Left.Roles);
    }

    /// <summary>
    /// An explicitly invoked workload receives captured Unicode input and an isolated mutable fixture copy.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_WorkloadReceivesInputAndIsolatedFiles()
    {
        using var files = new SessionWorkspaceFixture();
        var fixture = Path.Join(Path.GetDirectoryName(files.MarkerPath)!, "input.txt");
        await File.WriteAllTextAsync(fixture, "fixture λ", TestContext.CancellationToken);
        using var core = new ReplCore();
        Submit(core, ".method void Read() {", "ldstr \"input.txt\"", "call string File::ReadAllText(string)",
            "call void Console::Write(string)", "call class TextReader Console::get_In()",
            "callvirt instance string TextReader::ReadToEnd()", "call void Console::Write(string)",
            "ldstr \"input.txt\"", "ldstr \"changed in worker\"", "call void File::WriteAllText(string, string)", "ret", "}");
        var package = Prepare(core, ".jit Read --run --stdin \"é漢字🌍\" --files "
            + LiteralParser.Escape(Path.GetDirectoryName(fixture)!));

        var result = await ProcessNativeRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("complete", result.Outcome, Details(result));
        Assert.AreEqual(1, result.Left.Invocations);
        Assert.AreEqual("fixture λé漢字🌍", result.Left.StandardOutput);
        Assert.AreEqual("fixture λ", await File.ReadAllTextAsync(fixture, TestContext.CancellationToken));
        Assert.IsEmpty(result.Left.StandardError);
    }

    /// <summary>
    /// Exhausting a small iteration cap cannot claim Tier1 or execute more calls than explicitly allowed.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_Tier1IterationCapReportsActualCallsWithoutClaimingRequestedTier()
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Value(int32 value) { ldarg.0; ldc.i4.2; mul; ret }");
        var package = Prepare(core, ".jit Value (21) --tier tier1 --iterations 1 --timeout 2s");

        var result = await ProcessNativeRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreNotEqual("complete", result.Outcome, Details(result));
        Assert.AreEqual(1, result.Left.Invocations);
        Assert.DoesNotContain(compilation => compilation.Tier == "Tier1", result.Left.Compilations);
        Assert.IsNotNull(result.Left.Detail);
        Assert.Contains("tier", result.Left.Detail);
        Assert.IsEmpty(result.Left.StandardOutput);
    }

    /// <summary>
    /// An infinite printing workload reaches the output limit and returns its bounded captured output.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_PrintingWorkloadStopsAtOutputLimit()
    {
        using var core = new ReplCore();
        Submit(core, ".method void Print() { AGAIN: ldstr \"0123456789\"; call void Console::Write(string); br AGAIN }");
        var package = Prepare(core, ".jit Print --run");

        var result = await ProcessNativeRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("output-limit", result.Outcome, Details(result));
        Assert.AreEqual(1, result.Left.Invocations);
        Assert.AreEqual(64 * 1024, result.Left.StandardOutput.Length);
        Assert.StartsWith("01234567890123456789", result.Left.StandardOutput);
        Assert.Contains("64 KiB", result.Left.Detail!);
        Assert.IsEmpty(result.Left.StandardError);
    }

    /// <summary>
    /// Cancellation after the real body starts terminates its infinite loop and retains evidence of the one invocation.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_CancellationAfterBodyStartsTerminatesWorker()
    {
        using var files = new SessionWorkspaceFixture();
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(files.MarkerPath)!, Path.GetFileName(files.MarkerPath));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => entered.TrySetResult();
        watcher.EnableRaisingEvents = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var core = new ReplCore();
        Submit(core, ".method void Spin() {", "ldstr " + LiteralParser.Escape(files.MarkerPath), "ldstr \"entered\"",
            "call void File::WriteAllText(string, string)", "AGAIN: br AGAIN", "}");
        var package = Prepare(core, ".jit Spin --run");
        var running = ProcessNativeRunner.RunAsync(package, cancellation.Token);
        var observed = entered.Task.WaitAsync(TestContext.CancellationToken);
        if (await Task.WhenAny(running, observed) == running)
        {
            Assert.Fail("Worker finished before entering its body: " + Details(await running));
        }

        await observed;
        await cancellation.CancelAsync();

        var result = await running;

        Assert.AreEqual("cancelled", result.Outcome, Details(result));
        Assert.AreEqual(1, result.Left.Invocations);
        Assert.IsTrue(File.Exists(files.MarkerPath));
        Assert.Contains("cancelled", result.Left.Detail!);
    }

    /// <summary>
    /// Crashes and thrown exceptions preserve bounded output and identify the actual workload failure.
    /// </summary>
    /// <param name="crash">Whether the target exits its process instead of throwing an ordinary exception.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_WorkloadFailuresPreserveOutputAndActualCause(bool crash)
    {
        using var core = new ReplCore();
        Submit(core, ".method void Fail() {", "ldstr \"entered\"", "call void Console::Write(string)",
            crash ? "ldc.i4.s 23" : "ldstr \"native failure\"",
            crash ? "call void Environment::Exit(int32)" : "newobj instance void InvalidOperationException::.ctor(string)",
            crash ? "ret" : "throw", "}");
        var package = Prepare(core, ".jit Fail --run");

        var result = await ProcessNativeRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual(crash ? "crashed" : "failed", result.Outcome, Details(result));
        Assert.AreEqual(1, result.Left.Invocations);
        Assert.AreEqual("entered", result.Left.StandardOutput);
        Assert.Contains(crash ? "23" : "InvalidOperationException: native failure", result.Left.Detail!);
        Assert.DoesNotContain("TargetInvocationException", result.Left.Detail!);
        Assert.IsTrue(core.Status.CellIsEmpty);
    }

    /// <summary>
    /// Disabled CoreCLR diagnostics fails before an explicitly requested side-effecting workload can start.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_DisabledDiagnosticsRejectsBeforeWorkloadExecution()
    {
        using var files = new SessionWorkspaceFixture();
        using var core = new ReplCore();
        Submit(core, ".method void Write() {", "ldstr " + LiteralParser.Escape(files.MarkerPath), "ldstr \"executed\"",
            "call void File::WriteAllText(string, string)", "ret", "}");
        var package = Prepare(core, ".jit Write --run --env DOTNET_EnableDiagnostics=0");

        var result = await ProcessNativeRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("failed", result.Outcome, Details(result));
        Assert.Contains("DOTNET_EnableDiagnostics", result.Left.Detail!);
        Assert.Contains("must be 1", result.Left.Detail!);
        Assert.AreEqual(0, result.Left.Invocations);
        Assert.IsEmpty(result.Left.Compilations);
        Assert.IsFalse(File.Exists(files.MarkerPath));
    }

    /// <summary>
    /// Real grandchildren are terminated after both an orderly native worker completion and a process exit.
    /// </summary>
    /// <param name="crash">Whether the inspected method exits its worker process after the descendant starts.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Inspect_StopsRealGrandchildAfterWorkerEnds(bool crash)
    {
        using var files = new SessionWorkspaceFixture();
        var record = Path.Join(files.DirectoryPath, "processes");
        using var core = new ReplCore();
        core.Session.Resolver.Load(typeof(ComparisonDescendantSource).Assembly.Location);
        try
        {
            Submit(core, ".method void Work() {");
            foreach (var setting in new[]
            {
                "DOTNET_DiagnosticPorts",
                "DOTNET_JitStdOutFile",
                "DOTNET_JitDisasm",
                "DOTNET_JitDisasmSummary",
                "DOTNET_JitDisasmTesting",
                "DOTNET_JitDisasmWithCodeBytes",
            })
            {
                Submit(core, "ldstr " + LiteralParser.Escape(setting), "ldnull",
                    "call void Environment::SetEnvironmentVariable(string, string)");
            }

            Submit(core, "ldstr " + LiteralParser.Escape(Environment.ProcessPath!),
                "ldstr " + LiteralParser.Escape(files.DirectoryPath), "ldc.i4.1",
                "ldstr " + LiteralParser.Escape(crash ? "exit" : "return"),
                "ldc.i4.0",
                "call int32 [IlRepl.Tests]IlRepl.Tests.Engine.ComparisonDescendantSource::Run(string, string, bool, string, bool)",
                "call void Console::Write(int32)", "ret", "}");
            var package = Prepare(core, ".jit Work --run --timeout 60s");

            var result = await ProcessNativeRunner.RunAsync(package, TestContext.CancellationToken);

            Assert.AreEqual(crash ? "crashed" : "complete", result.Outcome, Details(result));
            Assert.AreEqual(1, result.Left.Invocations);
            if (!crash)
            {
                Assert.AreEqual("42", result.Left.StandardOutput);
            }

            var descendants = await File.ReadAllLinesAsync(record, TestContext.CancellationToken);
            Assert.HasCount(2, descendants);
            foreach (var descendant in descendants)
            {
                Assert.IsFalse(ComparisonDescendantSource.IsRunning(descendant), "descendant survived native cleanup: " + descendant);
            }
        }
        finally
        {
            if (File.Exists(record))
            {
                foreach (var descendant in await File.ReadAllLinesAsync(record, CancellationToken.None))
                {
                    await StopAsync(descendant);
                }

                static async Task StopAsync(string descendant)
                {
                    using var process = ComparisonDescendantSource.Open(descendant);
                    if (process is null || process.HasExited)
                    {
                        return;
                    }

                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
    }

    private static NativePackage Prepare(ReplCore core, string command)
    {
        var result = core.Handle(command);
        Assert.IsTrue(result.Succeeded, string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText)));
        Assert.IsNotNull(result.NativePackage);
        return result.NativePackage;
    }

    private static string Details(NativeReply result) => result.Left.Outcome + ": " + result.Left.Detail + "\n" + result.Left.StandardError;

    private static void Submit(ReplCore core, params string[] source)
    {
        foreach (var line in IlLines.Expand(source))
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line + "\n"
                + string.Join('\n', core.Transcript.Lines.Select(item => item.PlainText)));
        }
    }
}
