using System.Diagnostics;
using System.Globalization;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Real comparison workers isolate starting state and terminate failed executions without damaging the live session.
/// </summary>
[TestClass]
public sealed class MethodComparisonIsolationTests
{
    /// <summary>
    /// The cancellation token for worker processes and temporary-file coordination.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Both workers read independent fixture copies and identical stdin while parent files and working directory remain intact.
    /// </summary>
    [TestMethod]
    public async Task Run_FixtureDirectoriesAndStandardInputAreIndependent()
    {
        var fixture = Directory.CreateTempSubdirectory("ilrepl comparison fixture ");
        var parentDirectory = Environment.CurrentDirectory;
        try
        {
            var path = Path.Join(fixture.FullName, "input.txt");
            await File.WriteAllTextAsync(path, "seed", TestContext.CancellationToken);
            var session = IlLines.Load(".method string Read() {", "ldstr \"input.txt\"",
                "call string System.IO.File::ReadAllText(string)", "call void Console::Write(string)",
                "ldstr \"input.txt\"", "ldstr \"changed by worker\"", "call void System.IO.File::WriteAllText(string, string)",
                "call string Console::ReadLine()", "ret", "}");
            Commit(session, "Read");
            var package = ComparisonCapture.Create(session, "Copy () --files " + LiteralParser.Escape(fixture.FullName)
                + " --stdin " + LiteralParser.Escape("typed input\n"));

            var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

            Assert.AreEqual("match", result.Outcome, Details(result));
            Assert.HasCount(2, package.Files);
            Assert.AreEqual("input.txt", package.Files.Single(file => !file.IsDirectory).Path);
            Assert.AreEqual("seed", result.Original.StandardOutput);
            Assert.AreEqual("seed", result.Edited.StandardOutput);
            Assert.AreEqual("typed input", result.Original.Result!.Value);
            Assert.AreEqual("typed input", result.Edited.Result!.Value);
            Assert.AreEqual("seed", await File.ReadAllTextAsync(path, TestContext.CancellationToken));
            Assert.AreEqual(parentDirectory, Environment.CurrentDirectory);
            AssertParentUsable(session);
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Each worker receives the captured environment and its mutations cannot reach the other worker or the parent.
    /// </summary>
    /// <remarks>
    /// The variable exists only in the captured package, never in this process, so a worker that reads it cannot have inherited it.
    /// </remarks>
    [TestMethod]
    public async Task Run_EnvironmentUsesCapturedValuesAndIsolatesWrites()
    {
        var variable = "ILREPL_COMPARISON_" + Guid.NewGuid().ToString("N");
        var session = IlLines.Load(".method string Read() {", ".locals init (string original)",
            "ldstr " + LiteralParser.Escape(variable), "call string Environment::GetEnvironmentVariable(string)", "stloc.0",
            "ldstr " + LiteralParser.Escape(variable), "ldstr \"worker mutation\"",
            "call void Environment::SetEnvironmentVariable(string, string)", "ldloc.0", "ret", "}");
        Commit(session, "Read");
        var captured = ComparisonCapture.Create(session, "Copy ()");
        foreach (var (name, value) in captured.Environment)
        {
            Assert.AreEqual(Environment.GetEnvironmentVariable(name), value, name + " is captured as this process has it.");
        }

        var package = captured with
        {
            Environment = new Dictionary<string, string>(captured.Environment, StringComparer.Ordinal) { [variable] = "captured" },
        };

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("match", result.Outcome, Details(result));
        Assert.AreEqual("captured", result.Original.Result!.Value);
        Assert.AreEqual("captured", result.Edited.Result!.Value);
        Assert.IsNull(Environment.GetEnvironmentVariable(variable), "A worker's write must not reach this process.");
        AssertParentUsable(session);
    }

    /// <summary>
    /// Captured culture controls both workers even after the calling context restores its previous culture.
    /// </summary>
    [TestMethod]
    public async Task Run_CultureIsCapturedBeforeExecution()
    {
        var session = IlLines.Load(".method string Format() {", ".locals init (float64 number)", "ldc.r8 1234.5",
            "stloc.0", "ldloca 0", "call instance string Double::ToString()", "ret", "}");
        Commit(session, "Format");
        var previous = CultureInfo.CurrentCulture;
        ComparisonPackage package;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            package = ComparisonCapture.Create(session, "Copy ()");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("match", result.Outcome, Details(result));
        Assert.AreEqual("fr-FR", package.Culture);
        Assert.AreEqual("1234,5", result.Original.Result!.Value);
        Assert.AreEqual("1234,5", result.Edited.Result!.Value);
        Assert.AreEqual(previous, CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Fresh static initializers establish identical worker state after the live session has already mutated its own static field.
    /// </summary>
    [TestMethod]
    public async Task Run_StaticInitializersDoNotReplayExecutedParentCells()
    {
        var session = IlLines.Load(".class public Counter {", ".field public static int32 Value",
            ".method private static void .cctor() { ldc.i4 40; stsfld int32 Counter::Value; ret }",
            ".method public static int32 Next() { ldsfld int32 Counter::Value; ldc.i4.1; add;"
                + " dup; stsfld int32 Counter::Value; ret }", "}");
        session.AddLine("ldc.i4 99");
        session.AddLine("stsfld int32 Counter::Value");
        session.Run();
        Commit(session, "int32 Counter::Next()");

        var result = await Run(session, "Copy ()");

        Assert.AreEqual("match", result.Outcome, Details(result));
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("41", result.Edited.Result!.Value);
        session.ClearCell();
        session.AddLine("call int32 Counter::Next()");
        Assert.AreEqual(100, session.Run().Value);
        Assert.Contains("previously executed cells are not replayed", result.StartingState);
    }

    /// <summary>
    /// A real endless worker exceeds its execution limit and the parent can still evaluate a normal cell afterward.
    /// </summary>
    [TestMethod]
    public async Task Run_TimeoutTerminatesBothWorkersAndPreservesParent()
    {
        var session = IlLines.Load(".method int32 Spin() {", "ldstr \"stdout é\"", "call void Console::Write(string)",
            "call class System.IO.TextWriter Console::get_Error()", "ldstr \"stderr λ\"",
            "callvirt instance void System.IO.TextWriter::Write(string)", "AGAIN: br AGAIN", "}");
        Commit(session, "Spin");

        var result = await Run(session, "Copy () --timeout 1s");

        Assert.AreEqual("incomplete", result.Outcome, Details(result));
        Assert.AreEqual("timeout", result.Original.Outcome);
        Assert.AreEqual("timeout", result.Edited.Outcome);
        Assert.Contains("1000 ms", result.Original.Detail!);
        Assert.AreEqual("stdout é", result.Original.StandardOutput);
        Assert.AreEqual("stdout é", result.Edited.StandardOutput);
        Assert.AreEqual("stderr λ", result.Original.StandardError);
        Assert.AreEqual("stderr λ", result.Edited.StandardError);
        AssertParentUsable(session);
    }

    /// <summary>
    /// Exiting a real worker process is reported as a crash and cannot terminate the test process or live session.
    /// </summary>
    [TestMethod]
    public async Task Run_WorkerExitIsReportedAsCrash()
    {
        var session = IlLines.Load(".method int32 Exit() { ldc.i4 17; call void Environment::Exit(int32); ldc.i4.1; ret }");
        Commit(session, "Exit");

        var result = await Run(session, "Copy ()");

        Assert.AreEqual("incomplete", result.Outcome, Details(result));
        Assert.AreEqual("crashed", result.Original.Outcome);
        Assert.AreEqual("crashed", result.Edited.Outcome);
        Assert.Contains("17", result.Original.Detail!);
        AssertParentUsable(session);
    }

    /// <summary>
    /// Unbounded writes terminate at the configured output limit and cannot hang or flood the parent process.
    /// </summary>
    [TestMethod]
    public async Task Run_OutputLimitTerminatesUnboundedConsoleWrites()
    {
        var session = IlLines.Load(".method void Spam() { AGAIN: ldstr \"0123456789abcdef\";"
            + " call void Console::Write(string); br AGAIN }");
        Commit(session, "Spam");
        var package = ComparisonCapture.Create(session, "Copy ()") with { OutputLimit = 256 };

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("incomplete", result.Outcome, Details(result));
        Assert.AreEqual("output-limit", result.Original.Outcome);
        Assert.AreEqual("output-limit", result.Edited.Outcome);
        Assert.IsLessThanOrEqualTo(256, result.Original.StandardOutput.Length);
        Assert.IsLessThanOrEqualTo(256, result.Edited.StandardOutput.Length);
        AssertParentUsable(session);
    }

    /// <summary>
    /// Cancellation after a worker writes its real process ID terminates that worker and leaves the parent usable.
    /// </summary>
    [TestMethod]
    public async Task Run_CancellationKillsAnExecutingWorker()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-cancel-proof-");
        try
        {
            var path = Path.Join(directory.FullName, "started.pid");
            var session = IlLines.Load(".method int32 Spin() {", ".locals init (int32 pid)",
                "ldstr \"stdout é\"", "call void Console::Write(string)",
                "call class System.IO.TextWriter Console::get_Error()", "ldstr \"stderr λ\"",
                "callvirt instance void System.IO.TextWriter::Write(string)",
                "call int32 Environment::get_ProcessId()", "stloc.0", "ldstr " + LiteralParser.Escape(path),
                "ldloca 0", "call instance string Int32::ToString()", "call void System.IO.File::WriteAllText(string, string)",
                "AGAIN: br AGAIN", "}");
            Commit(session, "Spin");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            var running = ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"), cancellation.Token);
            try
            {
                while (!File.Exists(path))
                {
                    await Task.Delay(10, startup.Token);
                }

                var pid = int.Parse(await File.ReadAllTextAsync(path, startup.Token), CultureInfo.InvariantCulture);
                using var worker = Process.GetProcessById(pid);
                Assert.AreNotEqual(Environment.ProcessId, pid);
                await cancellation.CancelAsync();
                var result = await running;

                Assert.AreEqual("incomplete", result.Outcome, Details(result));
                Assert.AreEqual("cancelled", result.Original.Outcome);
                Assert.AreEqual("cancelled", result.Edited.Outcome);
                Assert.AreEqual("stdout é", result.Original.StandardOutput);
                Assert.AreEqual("stderr λ", result.Original.StandardError);
                Assert.AreEqual("", result.Edited.StandardOutput);
                Assert.AreEqual("", result.Edited.StandardError);
                Assert.IsTrue(worker.HasExited);
                AssertParentUsable(session);
            }
            finally
            {
                await cancellation.CancelAsync();
                await running;
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Equal native handles remain unavailable observations and cannot be reported as a successful match.
    /// </summary>
    [TestMethod]
    public async Task Run_UnavailableValuesCannotProduceMatch()
    {
        var session = IlLines.Load(".method native int Address() { ldc.i4.0; conv.i; ret }");
        Commit(session, "Address");

        var result = await Run(session, "Copy ()");

        Assert.AreEqual("incomplete", result.Outcome, Details(result));
        Assert.AreEqual("completed", result.Original.Outcome);
        Assert.AreEqual("unavailable", result.Original.Result!.Kind);
        Assert.AreEqual("unavailable", result.Edited.Result!.Kind);
        Assert.Contains("handles", result.Original.Result.Value!);
        AssertParentUsable(session);
    }

    private Task<ComparisonReply> Run(Session session, string command) =>
        ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, command), TestContext.CancellationToken);

    private static void Commit(Session session, string reference)
    {
        var edit = session.PrepareEdit(reference, "Copy");
        session.CommitEdit(edit.Name, edit.Source);
    }

    private static void AssertParentUsable(Session session)
    {
        session.ClearCell();
        session.AddLine("ldc.i4 42");
        Assert.AreEqual(42, session.Run().Value);
    }

    private static string Details(ComparisonReply result) =>
        $"{result.Outcome}: original={result.Original.Outcome} {result.Original.Detail}; "
        + $"edited={result.Edited.Outcome} {result.Edited.Detail}";
}
