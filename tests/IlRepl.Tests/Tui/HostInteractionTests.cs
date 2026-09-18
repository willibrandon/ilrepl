using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Exercises startup editing and explicitly confirmed interruption through a real terminal and execution host.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class HostInteractionTests
{
    /// <summary>
    /// Supplies cancellation to terminal and host operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The prompt accepts editing before the host connects and never submits retained input when readiness arrives.
    /// </summary>
    [TestMethod]
    public async Task Startup_EditingPrecedesHostReadiness()
    {
        var token = TestContext.CancellationToken;
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = new SessionController(async ct =>
        {
            await launch.Task.WaitAsync(ct);
            return await HostPaths.StartEngineAsync(ct);
        });
        PromptState? prompt = null;
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(controller, transcript, onPrompt: state => prompt = state);
        var run = IlReplApp.RunAsync(terminal, prompt, token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("starting execution host");
        await auto.TypeAsync("ldc.i4 42", ct: token);
        await auto.KeyAsync(Hex1bKey.Enter, ct: token);
        await auto.WaitUntilTextAsync("input retained");
        Assert.AreEqual("ldc.i4 42", prompt!.Text);
        Assert.AreEqual(SessionRuntimeState.Starting, controller.RuntimeState);
        launch.TrySetResult();
        await controller.Initialization.WaitAsync(token);
        await auto.WaitUntilAsync(_ => controller.RuntimeState == SessionRuntimeState.Ready);
        Assert.AreEqual(0, controller.Status.Instructions);
        Assert.AreEqual("ldc.i4 42", prompt.Text);
        await auto.KeyAsync(Hex1bKey.Enter, ct: token);
        await AppTest.TypeLinesAsync(auto, ["ret"], token);
        await auto.WaitUntilTextAsync("= 42 : int32");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run.WaitAsync(token);
    }

    /// <summary>
    /// A looping cell survives the first interrupt and is replaced only after its rendered escalation notice is confirmed.
    /// </summary>
    /// <param name="queueInput">Whether another submitted buffer awaits the looping cell.</param>
    /// <param name="body">The uncooperative managed loop, operating-system wait, or finally region.</param>
    [TestMethod]
    [DataRow(false, "loop")]
    [DataRow(true, "loop")]
    [DataRow(false, "sleep")]
    [DataRow(false, "finally")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Loop_RequiresDisplayedConfirmationAndPreservesDraft(bool queueInput, string body)
    {
        var token = TestContext.CancellationToken;
        var host = await HostPaths.StartEngineAsync(token);
        await using var controller = new SessionController(host, async ct => await HostPaths.StartEngineAsync(ct));
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.ProgressChanged += progress =>
        {
            if (progress.Phase == ExecutionPhase.UserCode && progress.IsRunning) running.TrySetResult();
        };
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(controller, new Transcript(), width: 120, onPrompt: state => prompt = state);
        var run = IlReplApp.RunAsync(terminal, prompt, token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        string[] instructions = body switch
        {
            "sleep" => ["ldc.i4.m1", "call void System.Threading.Thread::Sleep(int32)", "ret"],
            "finally" => [".try {", "leave DONE", "} finally {", "LOOP: br LOOP", "endfinally", "}", "DONE: ret"],
            _ => ["LOOP: br LOOP"],
        };
        await AppTest.TypeLinesAsync(auto, [".method void WaitForever() {", .. instructions, "}"], token);
        await auto.WaitUntilTextAsync("end of method WaitForever");
        await AppTest.TypeLinesAsync(auto, ["call void WaitForever()", "ret"], token);
        await running.Task.WaitAsync(token);
        if (queueInput)
        {
            await auto.TypeAsync("ldc.i4 73", ct: token);
            await auto.EnterAsync(ct: token);
            await auto.WaitUntilAsync(_ => prompt!.Pending.Count == 1);
        }
        await auto.TypeAsync("// keep this draft", ct: token);
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: token);
        await auto.WaitUntilTextAsync("Press Ctrl+C again to restart");
        Assert.AreEqual(SessionRuntimeState.Ready, controller.RuntimeState);
        Assert.IsTrue(controller.Progress.IsRunning);
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: token);
        await auto.WaitUntilTextAsync("runtime restarted; source and definitions retained");
        var expectedDraft = queueInput ? "ldc.i4 73\n// keep this draft" : "// keep this draft";
        Assert.AreEqual(expectedDraft, prompt!.Text);
        Assert.AreEqual(0, controller.Status.Instructions, "Retained queued input must require another explicit submission.");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: token);
        Assert.IsFalse(run.IsCompleted);
        Assert.AreEqual(expectedDraft, prompt.Text);
        Assert.AreEqual(SessionRuntimeState.Ready, controller.RuntimeState);
        Assert.AreEqual("interrupted", controller.Workspace!.Document.Cells.Single(cell => cell.Kind == "cell").State);
        await auto.Ctrl().KeyAsync(Hex1bKey.A, ct: token);
        await auto.BackspaceAsync(ct: token);
        await AppTest.TypeLinesAsync(auto, [".clear", "ldc.i4 42", "ret"], token);
        await auto.WaitUntilTextAsync("= 42 : int32");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run.WaitAsync(token);
    }

    /// <summary>
    /// A wrapped escalation notice still authorizes the next Ctrl+C and leaves the replacement editor focused.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Interrupt_WrappedNoticeAcceptsConfirmation()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var controller = await SessionWorkspaceFixture.StartAsync(token);
        foreach (var line in new[] { "ldstr " + LiteralParser.Escape(files.MarkerPath), "ldstr \"running\"",
            "call void System.IO.File::WriteAllText(string, string)", "LOOP: br LOOP" })
            Assert.IsTrue((await controller.HandleAsync(line, token)).Succeeded);
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(controller, new Transcript(), width: 12, height: 30,
            onPrompt: value => prompt = value);
        var run = IlReplApp.RunAsync(terminal, prompt, token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        var executing = controller.HandleAsync("ret", token);
        await auto.WaitUntilAsync(_ => File.Exists(files.MarkerPath));
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: token);
        await auto.WaitUntilAsync(snapshot => string.Concat(Enumerable.Range(0, snapshot.Height).Select(snapshot.GetLine))
            .Replace(" ", "", StringComparison.Ordinal).Contains("PressCtrl+Cagain", StringComparison.Ordinal));
        Assert.AreEqual(SessionRuntimeState.Ready, controller.RuntimeState);
        Assert.IsTrue(controller.Progress.IsRunning);
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: token);
        await auto.WaitUntilAsync(_ => controller.RuntimeState == SessionRuntimeState.Ready && !controller.Progress.IsRunning
            && controller.Workspace!.Document.Interruptions.Length == 1);
        await executing.WaitAsync(token);
        await auto.TypeAsync(".clear", ct: token);
        await auto.WaitUntilAsync(_ => prompt!.Text == ".clear");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run.WaitAsync(token);
    }
}
