using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// A block the engine refuses comes back whole, with the refused line selected and the engine
/// returned to where it stood before the block; what ran or committed stays.
/// </summary>
[TestClass]
public sealed class IlReplAppRecoveryTests
{
    private static readonly string[] s_twice = [".method int32 Twice(int32 n) {", "ldarg n", "ldc.i4 2", "mul", "ret", "}"];
    private static readonly string[] s_typo = [".method int32 F() {", "ldc.i4 1", "lcd.i4 2", "add", "ret", "}"];
    private static readonly string[] s_tryTypo = [".try {", "nop", "lcd.i4 1", "} catch [System.Runtime]System.Exception {", "pop", "}"];

    /// <summary>
    /// The test context, for cancellation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A typo in the middle of a method brings the whole method back with that line selected, and
    /// typing the correction over it makes the next Enter succeed.
    /// </summary>
    [TestMethod]
    public async Task TypeBlock_RefusedInMiddle_WholeDeclarationComesBack()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_typo, ct);
        await auto.WaitUntilTextAsync("unknown opcode 'lcd.i4'");
        await auto.WaitUntilTextAsync("method F abandoned; the block is back in the editor");
        await auto.WaitUntilTextAsync("editing 6 lines");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 2) == "  ...>   lcd.i4 2" && AppTest.CaretLine(s) == 2 && s.GetCell(9, AppTest.PromptTop(s) + 2).Background is not null && s.GetCell(9, AppTest.PromptTop(s) + 1).Background is null, description: "the refused line is selected and the caret is on it");
        Assert.AreSequenceEqual(["il[1]> .method int32 F() {", "il[1]>   ldc.i4 1", "il[1]>   lcd.i4 2"], AppTest.Echoes(transcript), "nothing after the refused line was sent");
        Assert.IsFalse(terminal.CreateSnapshot().ContainsText("method F │"), "the engine has no open method");
        Assert.AreEqual(0, engine.Status.OpenDepth);

        await auto.TypeAsync("  ldc.i4 2", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 2) == "  ...>   ldc.i4 2", description: "typing replaces the selected line");
        await auto.WaitUntilTextAsync("Enter sends 6 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method F");
        await AppTest.TypeLinesAsync(auto, ["call int32 F()", "ret"], ct);
        await auto.WaitUntilTextAsync("= 3 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A failure at the end of a method, where the return type is checked, leaves no method open:
    /// the whole declaration is editable and resubmitting it fails the same way, not with a
    /// nested-method error.
    /// </summary>
    [TestMethod]
    public async Task TypeBlock_CloseFails_WholeDeclarationComesBack()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method int32 F() {", "ldstr \"wrong\"", "ret", "}"], ct);
        await auto.WaitUntilTextAsync("ret needs int32 on the stack but found string");
        await auto.WaitUntilTextAsync("method F abandoned; the block is back in the editor");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 4 lines") && AppTest.PromptRow(s, 2) == "  ...>   ret" && AppTest.PromptRow(s, 3) == "  ...> }" && AppTest.CaretLine(s) == 2 && !s.ContainsText("opcodes"), description: "the whole declaration is back with the refused line selected and no palette");
        var first = transcript.Lines.Where(l => l.Kind == LineKind.Error).Select(l => l.PlainText).ToList();
        Assert.HasCount(1, first);
        Assert.AreEqual(0, engine.Status.OpenDepth, "the refused method is gone from the engine");

        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(_ => transcript.Lines.Count(l => l.Kind == LineKind.Error) == 2, description: "the resubmission is refused again");
        var second = transcript.Lines.Where(l => l.Kind == LineKind.Error).Select(l => l.PlainText).ToList();
        Assert.AreEqual(first[0], second[1], "the same close error, from a clean state");
        Assert.HasCount(2, transcript.Lines.Where(l => l.PlainText.Contains("method F abandoned", StringComparison.Ordinal)).ToList());
        Assert.HasCount(6, AppTest.Echoes(transcript), "both attempts stopped at the refused line");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A refused line inside a method inside a class brings the whole class back.
    /// </summary>
    [TestMethod]
    public async Task TypeClass_NestedMethodFails_WholeClassComesBack()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".class C {", ".method public static int32 M() {", "lcd.i4 1", "ret", "}", "}"], ct);
        await auto.WaitUntilTextAsync("unknown opcode 'lcd.i4'");
        await auto.WaitUntilTextAsync("class C abandoned");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.PromptRow(s, 0) == "il[1]> .class C {" && AppTest.PromptRow(s, 2) == "  ...>     lcd.i4 1" && AppTest.CaretLine(s) == 2, description: "the whole class is back with the refused line selected");
        Assert.IsFalse(terminal.CreateSnapshot().ContainsText("class C │"), "the engine has no open class");
        Assert.AreEqual(0, engine.Status.OpenDepth);

        await auto.TypeAsync("    ldc.i4 1", ct: ct);
        await auto.WaitUntilTextAsync("Enter sends 6 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of class C");
        await AppTest.TypeLinesAsync(auto, ["call int32 C::M()", "ret"], ct);
        await auto.WaitUntilTextAsync("= 1 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A second refusal brings the block back a second time; the third attempt commits once.
    /// </summary>
    [TestMethod]
    public async Task Recovery_SecondCorrection()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_typo, ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.CaretLine(s) == 2, description: "first refusal");
        await auto.TypeAsync("  ldc.i4 x", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(_ => transcript.Lines.Count(l => l.PlainText.Contains("method F abandoned", StringComparison.Ordinal)) == 2, description: "second refusal");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.PromptRow(s, 2) == "  ...>   ldc.i4 x" && AppTest.CaretLine(s) == 2, description: "the block is back again with the new bad line selected");
        Assert.AreEqual(0, engine.Status.OpenDepth);
        await auto.TypeAsync("  ldc.i4 2", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method F");
        Assert.HasCount(1, transcript.Lines.Where(l => l.PlainText.Contains("end of method F", StringComparison.Ordinal)).ToList());
        await AppTest.TypeLinesAsync(auto, ["call int32 F()", "ret"], ct);
        await auto.WaitUntilTextAsync("= 3 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Ctrl+C on a returned block clears it in one press, and the engine is already clean.
    /// </summary>
    [TestMethod]
    public async Task Recovery_CtrlC_LeavesEngineClean()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_typo, ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.CaretLine(s) == 2, description: "the block is back");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]>" && !s.ContainsText("editing"), description: "one Ctrl+C clears the returned block");
        Assert.IsFalse(run.IsCompleted);
        Assert.AreEqual(0, engine.Status.OpenDepth);
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        await auto.WaitUntilTextAsync("end of method Twice");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// The refused block and the corrected block are two history entries, each whole.
    /// </summary>
    [TestMethod]
    public async Task Recovery_CorrectedBlock_IsOneHistoryEntry()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_typo, ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.CaretLine(s) == 2, description: "the block is back");
        await auto.TypeAsync("  ldc.i4 2", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method F");
        await auto.WaitUntilNoTextAsync("editing");
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.PromptRow(s, 2) == "  ...>   ldc.i4 2" && AppTest.PromptRow(s, 5) == "  ...> }", description: "the corrected block is the newest entry");
        await auto.UpAsync(ct: ct);
        await auto.UpAsync(ct: ct);
        await auto.UpAsync(ct: ct);
        await auto.UpAsync(ct: ct);
        await auto.UpAsync(ct: ct);
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.PromptRow(s, 2) == "  ...>   lcd.i4 2", description: "the refused block is the entry before it");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Lines the cell accepted before the block stay applied when the block is withdrawn.
    /// </summary>
    [TestMethod]
    public async Task Recovery_EarlierTopLevelLinesStayApplied()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["ldc.i4 7"], ct);
        await auto.WaitUntilTextAsync("stack [int32]");
        await AppTest.TypeLinesAsync(auto, s_typo, ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && s.ContainsText("stack [int32]"), description: "the block is back and the value is still on the stack");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await AppTest.TypeLinesAsync(auto, ["ret"], ct);
        await auto.WaitUntilTextAsync("= 7 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A cell that throws when it runs stays run: only the units after it come back.
    /// </summary>
    [TestMethod]
    public async Task Paste_CellThrows_LaterUnitsComeBack_ExecutedUnitDoesNot()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync("ldc.i4 1\nldc.i4 0\ndiv\nret\nnop\n");
        await auto.WaitUntilTextAsync("Enter sends 5 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("threw System.DivideByZeroException");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[2]> nop" && !s.ContainsText("sending"), description: "the unsent line is back in the editor of the next cell");
        Assert.AreSequenceEqual(["il[1]> ldc.i4 1", "il[1]> ldc.i4 0", "il[1]> div", "il[1]> ret"], AppTest.Echoes(transcript), "the cell ran; nothing after it was sent");
        Assert.DoesNotContain(l => l.PlainText.Contains("withdrawn", StringComparison.Ordinal) || l.PlainText.Contains("abandoned", StringComparison.Ordinal), transcript.Lines, "a run cannot be withdrawn");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A destructive command inside a block moves the block's mark: a later refusal returns only
    /// the lines after it and says the earlier ones stayed applied.
    /// </summary>
    [TestMethod]
    public async Task Paste_UndoInsideBlock_ThenRefusal_ReturnsLinesAfterUndoWithNote()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(120, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync(".method int32 F() {\nldc.i4 1\n.undo\nlcd.i4 2\nret\n}\n");
        await auto.WaitUntilTextAsync("Enter sends 6 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("unknown opcode 'lcd.i4'");
        await auto.WaitUntilTextAsync("the lines before '.undo' stayed applied");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 3 lines") && AppTest.PromptRow(s, 0) == "il[1]> lcd.i4 2" && AppTest.PromptRow(s, 2) == "  ...> }" && AppTest.CaretLine(s) == 0, description: "only the lines after .undo are back, the refused one selected");
        Assert.IsTrue(terminal.CreateSnapshot().ContainsText("method F │"), "the method is still open in the engine");
        Assert.AreEqual(1, engine.Status.OpenDepth);

        await auto.TypeAsync("ldc.i4 2", ct: ct);
        await auto.WaitUntilTextAsync("Enter sends 3 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method F");
        await AppTest.TypeLinesAsync(auto, ["call int32 F()", "ret"], ct);
        await auto.WaitUntilTextAsync("= 2 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Ctrl+C that lands while the final brace is in flight cannot undo the commit: the method stays.
    /// </summary>
    [TestMethod]
    public async Task CtrlC_RacingFinalBrace_CommittedMethodStays()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        engine.Allow(5);
        await auto.WaitUntilTextAsync("sending 5/6");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilTextAsync("cancelling 5/6");
        engine.Allow(1);
        await auto.WaitUntilTextAsync("end of method Twice");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[2]>" && !s.ContainsText("cancelling"), description: "the block is done and the buffer is empty");
        Assert.DoesNotContain(l => l.PlainText.Contains("abandoned", StringComparison.Ordinal), transcript.Lines);
        engine.Allow(3);
        await AppTest.TypeLinesAsync(auto, ["ldc.i4 4", "call int32 Twice(int32)", "ret"], ct);
        await auto.WaitUntilTextAsync("= 8 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Ctrl+C in the middle of a block withdraws it and keeps its text.
    /// </summary>
    [TestMethod]
    public async Task CtrlC_MidBlock_RollsBackAndKeepsText()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        engine.Allow(3);
        await auto.WaitUntilTextAsync("sending 3/6");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilTextAsync("cancelling 3/6");
        engine.Allow(1);
        await auto.WaitUntilTextAsync("method Twice abandoned; the block is back in the editor");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.PromptRow(s, 0) == "il[1]> .method int32 Twice(int32 n) {" && AppTest.PromptRow(s, 5) == "  ...> }", description: "the whole block is back");
        Assert.AreEqual(0, engine.Status.OpenDepth);
        Assert.HasCount(4, engine.Handled);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A refused line inside a top-level exception region brings the whole region back and leaves
    /// the cell without the region; the corrected region is accepted once.
    /// </summary>
    [TestMethod]
    public async Task TryBlock_RefusedInMiddle_WholeRegionComesBack()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_tryTypo, ct);
        await auto.WaitUntilTextAsync("unknown opcode 'lcd.i4'");
        await auto.WaitUntilTextAsync("lines withdrawn; the block is back in the editor");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.PromptRow(s, 2) == "  ...>   lcd.i4 1" && AppTest.CaretLine(s) == 2 && !s.ContainsText("open block"), description: "the whole region is back and no region is open");
        Assert.AreEqual(0, engine.Status.OpenDepth);
        await auto.TypeAsync("  ldc.i4 1", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(_ => AppTest.Echoes(transcript).Count == 9, description: "the corrected region is sent whole");
        await AppTest.TypeLinesAsync(auto, [".show"], ct);
        await auto.WaitUntilAsync(_ => transcript.Lines.Any(l => l.Kind != LineKind.Input && l.PlainText.Contains(".try {", StringComparison.Ordinal)), description: ".show lists the region");
        Assert.HasCount(1, transcript.Lines.Where(l => l.Kind != LineKind.Input && l.PlainText.Contains(".try {", StringComparison.Ordinal)).ToList(), "the region was accepted once");
        Assert.AreEqual(0, engine.Status.OpenDepth);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A second refusal in a region returns it again; the third attempt is the only one accepted.
    /// </summary>
    [TestMethod]
    public async Task TryBlock_SecondCorrection()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_tryTypo, ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.CaretLine(s) == 2, description: "first refusal");
        await auto.TypeAsync("  ldc.i4 x", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(_ => transcript.Lines.Count(l => l.PlainText.Contains("lines withdrawn", StringComparison.Ordinal)) == 2, description: "second refusal");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.PromptRow(s, 2) == "  ...>   ldc.i4 x" && AppTest.CaretLine(s) == 2, description: "the region is back again");
        Assert.AreEqual(0, engine.Status.OpenDepth);
        await auto.TypeAsync("  ldc.i4 1", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(_ => AppTest.Echoes(transcript).Count == 12, description: "the third attempt is sent whole");
        await AppTest.TypeLinesAsync(auto, [".show"], ct);
        await auto.WaitUntilAsync(_ => transcript.Lines.Any(l => l.Kind != LineKind.Input && l.PlainText.Contains(".try {", StringComparison.Ordinal)), description: ".show lists the region");
        Assert.HasCount(1, transcript.Lines.Where(l => l.Kind != LineKind.Input && l.PlainText.Contains(".try {", StringComparison.Ordinal)).ToList());

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Ctrl+C inside a region withdraws it: the cell is back at depth zero.
    /// </summary>
    [TestMethod]
    public async Task TryBlock_CtrlC_MidRegion_LeavesDepthZero()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".try {", "nop", "ldc.i4 1", "} catch [System.Runtime]System.Exception {", "pop", "}"], ct);
        engine.Allow(2);
        await auto.WaitUntilTextAsync("sending 2/6");
        await auto.WaitUntilTextAsync("1 open block");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilTextAsync("cancelling 2/6");
        engine.Allow(1);
        await auto.WaitUntilTextAsync("lines withdrawn; the block is back in the editor");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && !s.ContainsText("open block"), description: "the region is back in the editor and gone from the cell");
        Assert.AreEqual(0, engine.Status.OpenDepth);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Nested regions come back whole, and the locals and stack from before them survive.
    /// </summary>
    [TestMethod]
    public async Task TryBlock_NestedRegionsWithEarlierLocals_ComeBackWhole()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript, 100, 40);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".locals init (int32 i)", "ldc.i4 5"], ct);
        await auto.WaitUntilTextAsync("1 local");
        await auto.WaitUntilTextAsync("stack [int32]");
        await AppTest.TypeLinesAsync(auto, [".try {", ".try {", "nop", "lcd.i4 1", "} finally {", "nop", "}", "} catch [System.Runtime]System.Exception {", "pop", "}"], ct);
        await auto.WaitUntilTextAsync("unknown opcode 'lcd.i4'");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 10 lines") && AppTest.PromptRow(s, 3) == "  ...>     lcd.i4 1" && AppTest.CaretLine(s) == 3 && s.ContainsText("1 local") && s.ContainsText("stack [int32]") && !s.ContainsText("open block"), description: "both regions are back; the local and the value stay");
        Assert.AreEqual(0, engine.Status.OpenDepth);
        await auto.TypeAsync("    ldc.i4 1", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(_ => AppTest.Echoes(transcript).Count == 16, description: "the corrected regions are sent whole");
        await AppTest.TypeLinesAsync(auto, [".show"], ct);
        await auto.WaitUntilAsync(_ => transcript.Lines.Count(l => l.Kind != LineKind.Input && l.PlainText.Contains(".try {", StringComparison.Ordinal)) == 2, description: ".show lists both regions once");
        Assert.AreEqual(0, engine.Status.OpenDepth);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// When the engine already has a region open, the buffer's lines belong to it until the depth
    /// returns to zero; a refusal rolls back to the region's state at the start of the buffer.
    /// </summary>
    [TestMethod]
    public async Task TryBlock_ContinuesRegionOpenedByScript()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        await engine.HandleAsync(".try {", ct);
        await engine.HandleAsync("nop", ct);
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.WaitUntilTextAsync("1 open block");
        await auto.TypeAsync("lcd.i4 1", ct: ct);
        await auto.WaitUntilTextAsync("Enter continues");
        await auto.EnterAsync(ct: ct);
        await AppTest.TypeLinesAsync(auto, ["} catch [System.Runtime]System.Exception {", "pop"], ct);
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilTextAsync("Enter sends 4 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("unknown opcode 'lcd.i4'");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 4 lines") && AppTest.PromptRow(s, 0) == "il[1]> lcd.i4 1" && AppTest.CaretLine(s) == 0 && s.ContainsText("1 open block"), description: "the lines are back and the script's region is still open");
        Assert.AreEqual(1, engine.Status.OpenDepth);
        await auto.TypeAsync("ldc.i4 1", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.Echoes(transcript).Count == 5 && !s.ContainsText("open block"), description: "the region closes");
        Assert.AreEqual(0, engine.Status.OpenDepth);
        await AppTest.TypeLinesAsync(auto, [".show"], ct);
        await auto.WaitUntilAsync(_ => transcript.Lines.Count(l => l.Kind != LineKind.Input && l.PlainText.Contains(".try {", StringComparison.Ordinal)) == 1, description: ".show lists one region");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// When Ctrl+C lands after the final brace committed, the queued text comes back to the
    /// editor rather than running behind a cancel.
    /// </summary>
    [TestMethod]
    public async Task CtrlC_RacingFinalBrace_QueuedTextComesBack()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new DelayedEngine(new InProcessEngine());
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        engine.Allow(5);
        await auto.WaitUntilTextAsync("sending 5/6");
        await AppTest.TypeLinesAsync(auto, ["nop"], ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]>" && s.ContainsText("sending 5/6"), description: "the line is queued behind the block");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilTextAsync("cancelling 5/6");
        engine.Allow(1);
        await auto.WaitUntilTextAsync("end of method Twice");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[2]> nop" && !s.ContainsText("cancelling"), description: "the block stays and the queued line is back in the editor");
        Assert.HasCount(6, engine.Handled, "the queued line did not run");
        engine.Allow(1);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("1 instruction");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A destructive command inside a block moves the boundary, and a run that fails after it
    /// stays run: the lines after the failed run come back, the block's close included.
    /// </summary>
    [TestMethod]
    public async Task Paste_ResetInsideBlock_ThenFailedRun_ReturnsLinesAfterIt()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync(".method void F() {\n.reset\nldnull\nthrow\nret\nldc.i4 42\n}\n");
        await auto.WaitUntilTextAsync("Enter sends 7 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("threw System.NullReferenceException");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 2 lines") && AppTest.PromptRow(s, 0).EndsWith("> ldc.i4 42", StringComparison.Ordinal) && AppTest.PromptRow(s, 1) == "  ...> }", description: "the lines after the failed run are back");
        Assert.DoesNotContain(l => l.PlainText.Contains("ldc.i4 42", StringComparison.Ordinal), transcript.Lines, "the lines after the run were never sent");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Once the returned block has been edited, a selection the user makes is theirs: Ctrl+C copies it.
    /// </summary>
    [TestMethod]
    public async Task Recovery_CtrlC_AfterEditing_CopiesTheSelection()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var recorder = new PresentationRecorder();
        await using var terminal = AppTest.Build(engine, transcript, configure: b => b.AddPresentationFilter(recorder));
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_typo, ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 6 lines") && AppTest.CaretLine(s) == 2, description: "the block is back");
        await auto.TypeAsync("  ldc.i4 2", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 2) == "  ...>   ldc.i4 2", description: "the correction replaced the line");
        await auto.Shift().KeyAsync(Hex1bKey.Home, ct: ct);
        await auto.WaitUntilAsync(s => s.GetCell(9, AppTest.PromptTop(s) + 2).Background is not null && AppTest.CaretAt(s, 7, 2), description: "the corrected line is selected");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilAsync(_ => recorder.Output.Contains("\x1b]52;c;", StringComparison.Ordinal), description: "the terminal is asked to copy");
        var payload = recorder.Output[(recorder.Output.LastIndexOf("\x1b]52;c;", StringComparison.Ordinal) + 7)..];
        payload = payload[..payload.IndexOfAny(['\x07', '\x1b'])];
        Assert.AreEqual("  ldc.i4 2", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        Assert.IsTrue(terminal.CreateSnapshot().ContainsText("editing 6 lines"), "the block stays in the editor");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }
}
