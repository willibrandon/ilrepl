using System.Diagnostics;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// A block goes to the engine line by line on a worker, so the screen, the keys, and the window
/// keep working while it is in flight. A gated engine holds the submission wherever a test wants.
/// </summary>
[TestClass]
public sealed class IlReplAppSubmissionTests
{
    private static readonly string[] s_twice = [".method int32 Twice(int32 n) {", "ldarg n", "ldc.i4 2", "mul", "ret", "}"];

    /// <summary>
    /// The test context, for cancellation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// While lines are in flight the status bar counts them and every echo appears as it lands.
    /// </summary>
    [TestMethod]
    public async Task Submit_WhileSending_Repaints()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        await auto.WaitUntilTextAsync("sending 0/6");
        await auto.WaitUntilTextAsync("Ctrl+C cancels");
        engine.Allow(2);
        await auto.WaitUntilTextAsync("sending 2/6");
        await auto.WaitUntilTextAsync("il[1]>   ldarg n");
        Assert.IsFalse(terminal.CreateSnapshot().ContainsText("il[1]>   mul"), "a line the engine has not answered is not echoed");
        engine.Allow(4);
        await auto.WaitUntilTextAsync("end of method Twice");
        await auto.WaitUntilTextAsync("il[2]>");
        await auto.WaitUntilNoTextAsync("sending");
        Assert.HasCount(6, engine.Handled);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A resize during a submission lays the screen out again at the new size.
    /// </summary>
    [TestMethod]
    public async Task Submit_WhileSending_ResizeRelayouts()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        engine.Allow(2);
        await auto.WaitUntilTextAsync("sending 2/6");
        adapter.Resize(70, 20);
        await auto.WaitUntilAsync(s => s.Width == 70 && s.Height == 20 && s.GetLine(19).Contains("sending 2/6", StringComparison.Ordinal) && AppTest.PromptRow(s, 0) == "il[1]>", description: "the status bar is on the new last row");
        adapter.Resize(120, 40);
        await auto.WaitUntilAsync(s => s.Width == 120 && s.GetLine(39).Contains("sending 2/6", StringComparison.Ordinal) && AppTest.PromptRow(s, 0) == "il[1]>", description: "and again after growing");
        engine.Allow(4);
        await auto.WaitUntilTextAsync("end of method Twice");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Enter on an empty buffer while lines are in flight does nothing: nothing extra is sent.
    /// </summary>
    [TestMethod]
    public async Task Submit_WhileSending_ExtraEnterDoesNothing()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        await auto.WaitUntilTextAsync("sending 0/6");
        await auto.EnterAsync(ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.EnterAsync(ct: ct);

        // A typed character proves the Enters before it have been handled while the block was in flight.
        await auto.TypeAsync("x", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> x" && s.ContainsText("sending 0/6"), description: "the Enters were handled while busy and sent nothing");
        await auto.BackspaceAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]>", description: "the buffer is empty again");
        engine.Allow(6);
        await auto.WaitUntilTextAsync("end of method Twice");
        await auto.WaitUntilTextAsync("il[2]>");
        await auto.WaitUntilNoTextAsync("sending");
        Assert.HasCount(6, engine.Handled, "exactly the block's lines were handled");
        Assert.HasCount(6, AppTest.Echoes(transcript));
        Assert.AreEqual("il[2]>", AppTest.PromptRow(terminal.CreateSnapshot(), 0));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Typing goes on while lines are in flight, and Enter on that text queues it for after the block.
    /// </summary>
    [TestMethod]
    public async Task Submit_WhileSending_TypingQueuesForAfter()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        await auto.WaitUntilTextAsync("sending 0/6");
        await auto.TypeAsync("nop", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> nop" && s.ContainsText("sending 0/6"), description: "typing lands in the buffer while the block is in flight");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]>" && engine.Handled.Count == 0, description: "the line is queued, not sent ahead of the block");
        engine.Allow(7);
        await auto.WaitUntilTextAsync("il[2]> nop");
        await auto.WaitUntilTextAsync("1 instruction");
        Assert.AreSequenceEqual([.. s_twice, "nop"], engine.Handled.Select(l => l.Trim()).ToList(), "the block went first, then the queued line");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// The arrows and Shift selection work on the buffer while lines are in flight.
    /// </summary>
    [TestMethod]
    public async Task Submit_WhileSending_ArrowsAndSelectionWork()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        await auto.WaitUntilTextAsync("sending 0/6");
        await auto.TypeAsync("abc", ct: ct);
        await auto.LeftAsync(ct: ct);
        await auto.LeftAsync(ct: ct);
        await auto.TypeAsync("x", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> axbc" && AppTest.CaretAt(s, 9, 0), description: "the arrows moved the caret");
        await auto.Shift().KeyAsync(Hex1bKey.Home, ct: ct);
        await auto.TypeAsync("y", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> ybc", description: "typing replaced the selection");
        Assert.IsTrue(terminal.CreateSnapshot().ContainsText("sending 0/6"), "the block is still in flight");
        engine.Allow(6);
        await auto.WaitUntilTextAsync("end of method Twice");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[2]>", description: "Ctrl+C clears the buffer once the block is done");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Ctrl+C during a block waits for the line in flight, withdraws the block, and leaves its text
    /// in the editor.
    /// </summary>
    [TestMethod]
    public async Task Submit_CtrlC_CancelsAndKeepsRemainder()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        engine.Allow(2);
        await auto.WaitUntilTextAsync("sending 2/6");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilTextAsync("cancelling 2/6");
        engine.Allow(1);
        await auto.WaitUntilTextAsync("method Twice abandoned; the block is back in the editor");
        await auto.WaitUntilTextAsync("editing 6 lines");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> .method int32 Twice(int32 n) {" && AppTest.PromptRow(s, 5) == "  ...> }" && !s.ContainsText("sending"), description: "the whole block is back");
        Assert.HasCount(3, engine.Handled, "the line in flight was answered before the cancel took effect");
        Assert.AreEqual(0, engine.Status.OpenDepth);
        Assert.IsFalse(terminal.CreateSnapshot().ContainsText("method Twice │"), "no method is open");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A transport failure mid-block prints the error, withdraws the block, and keeps its text.
    /// </summary>
    [TestMethod]
    public async Task Submit_EngineThrows_KeepsRemainderAndShowsError()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new FaultingEngine(new InProcessEngine(), failAt: 3);
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        await auto.WaitUntilTextAsync("engine error: the host has exited");
        await auto.WaitUntilTextAsync("method Twice abandoned; the block is back in the editor");
        await auto.WaitUntilTextAsync("editing 6 lines");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 3) == "  ...>   mul" && !s.ContainsText("sending"), description: "the whole block is back");
        Assert.AreSequenceEqual(["il[1]> .method int32 Twice(int32 n) {", "il[1]>   ldarg n", "il[1]>   ldc.i4 2"], AppTest.Echoes(transcript));
        Assert.AreEqual(0, engine.Status.OpenDepth);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// When the host dies under a block the error is printed and the text stays in the editor.
    /// </summary>
    [TestMethod]
    public async Task Submit_HostDies_ShowsErrorAndKeepsText()
    {
        var ct = TestContext.CancellationToken;
        var host = await HostPaths.StartEngineAsync(ct);
        await using var engine = new DelayedEngine(host);
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(30));

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        engine.Allow(2);
        await auto.WaitUntilTextAsync("sending 2/6");
        Process.GetProcessById(host.ProcessId).Kill();
        engine.Allow(1);
        await auto.WaitUntilTextAsync("engine error:");
        await auto.WaitUntilTextAsync("editing 6 lines");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> .method int32 Twice(int32 n) {" && AppTest.PromptRow(s, 5) == "  ...> }" && !s.ContainsText("sending"), description: "the whole block is back");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Ctrl+Q quits even while a block is in flight.
    /// </summary>
    [TestMethod]
    public async Task Submit_Quit_StopsApp()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        engine.Allow(2);
        await auto.WaitUntilTextAsync("sending 2/6");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run.WaitAsync(AppTest.Timeout, ct);
        engine.Allow(4);
    }
}
