using System.Diagnostics;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Engine;
using IlRepl.Processes;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Interrupts actual batch frontends through their terminal while observing partial output and execution-process cleanup.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class BatchInterruptTests
{
    /// <summary>
    /// Supplies cancellation for real terminal, filesystem, and process operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Waiting for another input line remains interruptible after a completed batch cell.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task CtrlC_WhileWaitingForInputExitsPromptly()
    {
        var token = TestContext.CancellationToken;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithPtyProcess(options =>
        {
            options.FileName = HostLocator.FindDotnet();
            options.Arguments = [RepoPaths.FrontEndAssembly, "--batch", "--no-color"];
            options.WorkingDirectory = RepoPaths.Root;
        }).WithHeadless().WithDimensions(100, 30).Build();

        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(20));
        await auto.TypeAsync("ldc.i4 42", ct: token);
        await auto.EnterAsync(ct: token);
        await auto.TypeAsync("ret", ct: token);
        await auto.EnterAsync(ct: token);
        await auto.WaitUntilTextAsync("= 42 : int32");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: token);
        Assert.AreEqual(130, await run.WaitAsync(TimeSpan.FromSeconds(20), token));
    }

    /// <summary>
    /// Windows end-of-input remains a successful batch exit rather than being mistaken for console interruption.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task CtrlZ_WhileWaitingForInputExitsNormally()
    {
        var token = TestContext.CancellationToken;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithPtyProcess(options =>
        {
            options.FileName = HostLocator.FindDotnet();
            options.Arguments = [RepoPaths.FrontEndAssembly, "--batch", "--no-color"];
            options.WorkingDirectory = RepoPaths.Root;
        }).WithHeadless().WithDimensions(100, 30).Build();

        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(20));
        await auto.TypeAsync("ldc.i4 42", ct: token);
        await auto.EnterAsync(ct: token);
        await auto.TypeAsync("ret", ct: token);
        await auto.EnterAsync(ct: token);
        await auto.WaitUntilTextAsync("= 42 : int32");
        await auto.Ctrl().KeyAsync(Hex1bKey.Z, ct: token);
        await auto.EnterAsync(ct: token);
        Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(20), token));
    }

    /// <summary>
    /// Expressions, stdin, and explicit session replay all flush partial output and exit 130 without recovery.
    /// </summary>
    /// <param name="mode">The actual CLI source mode.</param>
    [TestMethod]
    [DataRow("eval")]
    [DataRow("stdin")]
    [DataRow("run")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task CtrlC_TerminatesBatchWithPartialOutput(string mode)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var marker = Path.Join(files.DirectoryPath, "host.pid");
        string[] source =
        [
            "ldstr " + LiteralParser.Escape(marker), "call int32 System.Environment::get_ProcessId()",
            "call string System.Convert::ToString(int32)", "call void System.IO.File::WriteAllText(string, string)",
            "ldstr \"batch partial output\"", "call void System.Console::WriteLine(string)", "LOOP: br LOOP", "ret",
        ];
        if (mode == "run")
        {
            await using var engine = new InProcessEngine();
            foreach (var line in source[..^1])
            {
                Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded);
            }

            var captured = await engine.SessionAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.Capture },
            }, token);

            await File.WriteAllBytesAsync(files.SessionPath, SessionCodec.Write(captured.Document), token);
        }

        string[] arguments = mode switch
        {
            "eval" => ["--no-color", "--eval", string.Join(';', source)],
            "run" => ["--no-color", "--session", files.SessionPath, "--run"],
            _ => ["--no-color", "--batch"],
        };

        var recorder = new WorkloadRecorder();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithPtyProcess(options =>
        {
            options.FileName = HostLocator.FindDotnet();
            options.Arguments = [RepoPaths.FrontEndAssembly, .. arguments];
            options.WorkingDirectory = files.DirectoryPath;
        }).AddWorkloadFilter(recorder).WithHeadless().WithDimensions(100, 30).Build();

        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(20));
        if (mode == "stdin")
        {
            foreach (var line in source)
            {
                await auto.TypeAsync(line, ct: token);
                await auto.EnterAsync(ct: token);
            }
        }

        await auto.WaitUntilAsync(_ => File.Exists(marker) && new FileInfo(marker).Length != 0);
        var hostId = int.Parse(await File.ReadAllTextAsync(marker, token));
        using var host = Process.GetProcessById(hostId);
        var scope = OwnedProcessGroup.Describe(host, "batch-interrupt");
        await auto.WaitUntilTextAsync("batch partial output");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: token);
        Assert.AreEqual(130, await run.WaitAsync(TimeSpan.FromSeconds(20), token), recorder.Output);
        await OwnedProcessGroup.WaitForExitAsync(scope, token);
        Assert.IsFalse(OwnedProcessGroup.IsRunning(scope));
        Assert.Contains("batch partial output", recorder.Output);
        Assert.DoesNotContain("runtime restarted", recorder.Output);
    }
}
