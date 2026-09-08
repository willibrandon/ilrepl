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
    /// Enter while lines are in flight waits its turn: the buffer was cleared when the block was
    /// sent, so the block cannot go twice, and each Enter on the empty buffer is one blank line,
    /// run after the block in order.
    /// </summary>
    [TestMethod]
    public async Task Submit_WhileSending_ExtraEnterQueuesAfterBlock()
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

        // A typed character proves the Enters before it have been handled while the block was in flight.
        await auto.TypeAsync("q", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> q" && s.ContainsText("sending 0/6") && engine.Handled.Count == 0, description: "the Enters were handled while busy and nothing was sent ahead of the block");
        await auto.BackspaceAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]>", description: "the buffer is empty again");
        engine.Allow(6);
        await auto.WaitUntilTextAsync("end of method Twice");
        await auto.WaitUntilTextAsync("sending 0/1");
        engine.Allow(2);
        await auto.WaitUntilAsync(s => !s.ContainsText("sending") && engine.Handled.Count == 8, description: "the two blank lines went after the block");
        Assert.AreSequenceEqual([.. s_twice.Select((l, i) => i == 0 || i == 5 ? l : "  " + l), "", ""], engine.Handled);
        Assert.DoesNotContain(l => l.Kind == LineKind.Error, transcript.Lines, "a blank line on an empty cell is not an error");
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
        await auto.TypeAsync("q", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> aqbc" && AppTest.CaretAt(s, 9, 0), description: "the arrows moved the caret");
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
    /// Ctrl+Q quits even while a block is in flight, and settling afterwards waits for the line
    /// with the engine and sends nothing more.
    /// </summary>
    [TestMethod]
    public async Task Submit_Quit_StopsApp()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(engine, transcript, onPrompt: p => prompt = p);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        engine.Allow(2);
        await auto.WaitUntilTextAsync("sending 2/6");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run.WaitAsync(AppTest.Timeout, ct);

        var settle = IlReplApp.SettleAsync(prompt!);
        Assert.IsFalse(settle.IsCompleted, "the line in flight is still with the engine");
        engine.Allow(1);
        await settle.WaitAsync(AppTest.Timeout, ct);
        Assert.HasCount(3, engine.Handled, "the line in flight went by and nothing after it");
        Assert.AreEqual(0, engine.Waiting);
    }

    /// <summary>
    /// Ctrl+C between two top-level lines keeps the line that had not started: it comes back to
    /// the editor, nothing is withdrawn, and the lines that went by stay.
    /// </summary>
    [TestMethod]
    public async Task Submit_CtrlC_BetweenTopLevelLines_KeepsTheUnsentLine()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync("ldc.i4 1\nldc.i4 2\nldc.i4 3\n");
        await auto.WaitUntilTextAsync("Enter sends 3 lines");
        await auto.EnterAsync(ct: ct);
        engine.Allow(1);
        await auto.WaitUntilTextAsync("sending 1/3");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilTextAsync("cancelling 1/3");
        engine.Allow(1);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> ldc.i4 3" && !s.ContainsText("cancelling"), description: "the line that had not started is back");
        await auto.WaitUntilTextAsync("stack [int32, int32]");
        Assert.HasCount(2, engine.Handled);
        Assert.DoesNotContain(l => l.PlainText.Contains("withdrawn", StringComparison.Ordinal), transcript.Lines, "nothing was withdrawn");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Text queued behind a block comes back to the editor with the block when Ctrl+C withdraws it.
    /// </summary>
    [TestMethod]
    public async Task Submit_CtrlC_WithQueuedText_ReturnsItToEditor()
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
        await AppTest.TypeLinesAsync(auto, ["nop"], ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]>" && s.ContainsText("sending 2/6"), description: "the line is queued behind the block");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilTextAsync("cancelling 2/6");
        engine.Allow(1);
        await auto.WaitUntilTextAsync("method Twice abandoned; the block is back in the editor");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 7 lines") && AppTest.PromptRow(s, 0) == "il[1]> .method int32 Twice(int32 n) {" && AppTest.PromptRow(s, 6) == "  ...> nop", description: "the block and the queued line are both back");
        Assert.HasCount(3, engine.Handled);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A line typed while a block is in flight is judged against where the block will leave the
    /// engine, not against the method the worker is still closing: it queues as a complete line.
    /// </summary>
    [TestMethod]
    public async Task Submit_WhileSending_TypedLineQueuesAsComplete()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        engine.Allow(1);
        await auto.WaitUntilTextAsync("sending 1/6");
        await auto.WaitUntilTextAsync("method Twice │");
        await AppTest.TypeLinesAsync(auto, ["nop"], ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]>" && !s.ContainsText("editing") && s.ContainsText("sending 1/6"), description: "the line queued instead of continuing a block");
        engine.Allow(5);
        await auto.WaitUntilTextAsync("end of method Twice");
        engine.Allow(1);
        await auto.WaitUntilTextAsync("il[2]> nop");
        Assert.AreEqual("nop", engine.Handled[^1]);
        Assert.HasCount(7, engine.Handled);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A failure nobody planned for, from the engine or the store, still hands the unsent text
    /// back and withdraws the block in flight.
    /// </summary>
    [TestMethod]
    public async Task Submit_EngineThrowsUnexpectedly_KeepsRemainderAndShowsError()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new FaultingEngine(new InProcessEngine(), failAt: 3, () => new InvalidOperationException("the engine lost its footing"));
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        await auto.WaitUntilTextAsync("engine error: the engine lost its footing");
        await auto.WaitUntilTextAsync("method Twice abandoned; the block is back in the editor");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.PromptRow(s, 0) == "il[1]> .method int32 Twice(int32 n) {" && AppTest.PromptRow(s, 5) == "  ...> }" && !s.ContainsText("sending"), description: "the whole block is back");
        Assert.AreEqual(0, engine.Status.OpenDepth);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Enter on an empty buffer while a block is in flight queues a run; when the block is
    /// withdrawn, that run comes back as a blank line after the block, and resending both
    /// commits the block and runs the cell.
    /// </summary>
    [TestMethod]
    public async Task Submit_CtrlC_WithQueuedBlankRun_ReturnsItAsABlankLine()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        engine.Allow(1);
        await AppTest.TypeLinesAsync(auto, ["ldc.i4 7"], ct);
        await auto.WaitUntilTextAsync("stack [int32]");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        engine.Allow(2);
        await auto.WaitUntilTextAsync("sending 2/6");
        await auto.EnterAsync(ct: ct);
        await auto.TypeAsync("q", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> q" && s.ContainsText("sending 2/6"), description: "the blank run is queued behind the block");
        await auto.BackspaceAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]>", description: "the buffer is empty again");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilTextAsync("cancelling 2/6");
        engine.Allow(1);
        await auto.WaitUntilTextAsync("method Twice abandoned; the block is back in the editor");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 7 lines") && AppTest.PromptRow(s, 5) == "  ...> }" && AppTest.PromptRow(s, 6) == "  ...>", description: "the block is back with the blank run after it");
        await auto.WaitUntilTextAsync("Enter sends 7 lines");
        engine.Allow(7);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method Twice");
        await auto.WaitUntilAsync(_ => transcript.Lines.Any(l => l.Kind == LineKind.Result), description: "the blank line ran the cell that was waiting");
        Assert.AreEqual("", engine.Handled[^1]);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A run cancelled by the token still settles: the line with the engine is waited for and
    /// nothing after it is sent.
    /// </summary>
    [TestMethod]
    public async Task Submit_Cancelled_SettlesBeforeReturning()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(engine, transcript, onPrompt: p => prompt = p);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = IlReplApp.RunAsync(terminal, prompt, cancel.Token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        engine.Allow(2);
        await auto.WaitUntilTextAsync("sending 2/6");
        await cancel.CancelAsync();
        await auto.WaitUntilAsync(_ => prompt!.Submission is { CancelRequested: true } && engine.Waiting == 1, description: "the run has asked the worker to stop and waits for the line with the engine");
        Assert.IsFalse(run.IsCompleted, "the run waits for the line in flight");
        engine.Allow(1);
        try
        {
            await run.WaitAsync(AppTest.Timeout, ct);
        }
        catch (OperationCanceledException)
        {
            // The token ended the run; what matters is what was settled first.
        }

        Assert.HasCount(3, engine.Handled, "the line in flight went by and nothing after it");
        Assert.AreEqual(0, engine.Waiting);
    }
}
