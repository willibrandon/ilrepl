using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Editing a block in the real prompt: indentation, comments, selection, history, the palette,
/// and the colours a line wears before and after Enter.
/// </summary>
[TestClass]
public sealed class IlReplAppBlockTests
{
    private static readonly string[] s_twice = [".method int32 Twice(int32 n) {", "ldarg n", "ldc.i4 2", "mul", "ret", "}"];

    /// <summary>
    /// The test context, for cancellation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A block comment that spans lines inside a method is one comment: the text after its close
    /// delimiter is the instruction, and the method compiles and runs.
    /// </summary>
    [TestMethod]
    public async Task TypeBlock_WithMultiLineComment_Compiles()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method int32 F() {", "/* open", "still */ ldc.i4.1", "ret"], ct);
        await auto.WaitUntilTextAsync("editing 5 lines");
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilTextAsync("Enter sends 5 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method F");
        await auto.WaitUntilTextAsync("il[2]>");
        await AppTest.TypeLinesAsync(auto, ["call int32 F()", "ret"], ct);
        await auto.WaitUntilTextAsync("= 1 : int32");

        Assert.AreSequenceEqual(["il[1]> .method int32 F() {", "il[1]>   /* open", "il[1]>   still */ ldc.i4.1", "il[1]>   ret", "il[1]> }", "il[2]> call int32 F()", "il[2]> ret"], AppTest.Echoes(transcript));
        Assert.DoesNotContain(l => l.Kind == LineKind.Error, transcript.Lines);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A line that is only a comment is echoed and ignored: the value stays on the stack.
    /// </summary>
    [TestMethod]
    public async Task CommentLine_WithPendingCell_DoesNotRun()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["ldc.i4 1", "// note"], ct);
        await auto.WaitUntilTextAsync("il[1]> // note");
        await auto.WaitUntilAsync(s => s.ContainsText("stack [int32]"), description: "the value is still on the stack");
        Assert.DoesNotContain(l => l.Kind == LineKind.Result, transcript.Lines, "a comment never runs the cell");
        await AppTest.TypeLinesAsync(auto, ["ret"], ct);
        await auto.WaitUntilTextAsync("= 1 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Enter in the middle of a line splits it at the caret and indents the new line.
    /// </summary>
    [TestMethod]
    public async Task Enter_MidLine_SplitsAtCaretAndIndents()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync(".method void F() {nop", ct: ct);
        await auto.WaitUntilTextAsync("il[1]> .method void F() {nop");
        await auto.LeftAsync(ct: ct);
        await auto.LeftAsync(ct: ct);
        await auto.LeftAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.CaretAt(s, 7 + ".method void F() {".Length, 0), description: "caret before nop");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> .method void F() {" && AppTest.PromptRow(s, 1) == "  ...>   nop" && AppTest.CaretAt(s, 9, 1), description: "nop moved to an indented line with the caret before it");
        await auto.WaitUntilTextAsync("editing 2 lines");
        Assert.IsEmpty(AppTest.Echoes(transcript));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A close brace typed inside a block comment is text, not structure: it keeps its indentation.
    /// </summary>
    [TestMethod]
    public async Task CloseBrace_InsideBlockComment_NoDedent()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method void F() {", "/* a"], ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 1) == "  ...>   /* a" && AppTest.CaretAt(s, 9, 2), description: "the comment line is indented and the next line copies its indentation");
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 2) == "  ...>   }" && AppTest.CaretAt(s, 10, 2), description: "the brace stays indented inside the comment");
        await auto.WaitUntilTextAsync("Enter continues");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Undo after a dedented close brace brings back the indentation and the caret in one step.
    /// </summary>
    [TestMethod]
    public async Task CloseBrace_Undo_RestoresIndentAndCaret()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method void F() {", "nop"], ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 1) == "  ...>   nop" && AppTest.CaretAt(s, 9, 2), description: "an indented blank third line");
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 2) == "  ...> }" && AppTest.CaretAt(s, 8, 2), description: "the brace dedents");
        await auto.WaitUntilTextAsync("Enter sends 3 lines");
        await auto.Ctrl().KeyAsync(Hex1bKey.Z, ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 2) == "  ...>" && AppTest.CaretAt(s, 9, 2), description: "undo restores the indentation and the caret after it");
        await auto.WaitUntilTextAsync("Enter continues");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Redo after that undo applies the dedent and the brace again as one step.
    /// </summary>
    [TestMethod]
    public async Task CloseBrace_Redo_ReappliesDedent()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method void F() {", "nop"], ct);
        await auto.WaitUntilAsync(s => AppTest.CaretAt(s, 9, 2), description: "an indented blank third line");
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 2) == "  ...> }", description: "the brace dedents");
        await auto.Ctrl().KeyAsync(Hex1bKey.Z, ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 2) == "  ...>" && AppTest.CaretAt(s, 9, 2), description: "undone");
        await auto.Ctrl().KeyAsync(Hex1bKey.Y, ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 2) == "  ...> }" && AppTest.CaretAt(s, 8, 2), description: "redo brings the dedented brace back");
        await auto.WaitUntilTextAsync("Enter sends 3 lines");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Exception regions nest like the listings show them: each open brace steps in by two, and a
    /// close brace steps out before the rest of its line.
    /// </summary>
    [TestMethod]
    public async Task Nested_TryCatch_IndentsLikeShow()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method void F() {", ".try {", "nop", "leave.s L", "} catch [System.Runtime]System.Exception {", "pop", "leave.s L", "}", "L: ret"], ct);
        await auto.TypeAsync("}", ct: ct);
        string[] rows =
        [
            "il[1]> .method void F() {",
            "  ...>   .try {",
            "  ...>     nop",
            "  ...>     leave.s L",
            "  ...>   } catch [System.Runtime]System.Exception {",
            "  ...>     pop",
            "  ...>     leave.s L",
            "  ...>   }",
            "  ...>   L: ret",
            "  ...> }",
        ];
        await auto.WaitUntilAsync(s =>
        {
            // The editor shows the last rows that fit; each must sit at its region's depth.
            var visible = s.Height - 1 - AppTest.PromptTop(s);
            return visible > 4 && rows.TakeLast(visible).Select((r, i) => AppTest.PromptRow(s, i) == r).All(b => b);
        }, description: "every row sits at its region's depth");
        await auto.WaitUntilTextAsync("Enter sends 10 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method F");
        Assert.DoesNotContain(l => l.Kind == LineKind.Error, transcript.Lines);
        Assert.AreSequenceEqual(rows.Select(r => "il[1]> " + r[7..]).ToList(), AppTest.Echoes(transcript), "the echo keeps the indentation the editor gave each line");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// An unterminated block comment keeps the buffer open; once closed, both lines go by as comments.
    /// </summary>
    [TestMethod]
    public async Task Enter_OpenComment_Continues()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("/* note", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("  ...> ");
        await auto.WaitUntilTextAsync("Enter continues");
        await auto.TypeAsync("*/", ct: ct);
        await auto.WaitUntilTextAsync("Enter sends 2 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("il[1]> */");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]>", description: "the prompt is empty and still the first cell");
        Assert.AreSequenceEqual(["il[1]> /* note", "il[1]> */"], AppTest.Echoes(transcript));
        Assert.DoesNotContain(l => l.Kind is LineKind.Error or LineKind.Result, transcript.Lines);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Shift+Down inside a multi-line buffer selects source lines rather than the transcript.
    /// </summary>
    [TestMethod]
    public async Task ShiftDown_InBuffer_SelectsSourceLines()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method void F() {"], ct);
        await auto.TypeAsync("nop", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 1) == "  ...>   nop", description: "two lines");
        await auto.UpAsync(ct: ct);
        await auto.HomeAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.CaretAt(s, 7, 0), description: "caret at the start of the first line");
        await auto.Shift().KeyAsync(Hex1bKey.DownArrow, ct: ct);
        await auto.WaitUntilAsync(s => AppTest.CaretAt(s, 7, 1) && s.GetCell(8, AppTest.PromptTop(s)).Background is not null && s.GetCell(9, AppTest.PromptTop(s) + 1).Background is null, description: "the first line is selected and the second is not");
        await auto.WaitUntilTextAsync("editing 2 lines");
        Assert.IsFalse(terminal.CreateSnapshot().ContainsText("y yank"), "the transcript's copy mode must not start");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Typing over a selection replaces it.
    /// </summary>
    [TestMethod]
    public async Task Selection_Typing_Replaces()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method void F() {"], ct);
        await auto.TypeAsync("nop", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 1) == "  ...>   nop", description: "two lines");
        await auto.UpAsync(ct: ct);
        await auto.HomeAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.CaretAt(s, 7, 0), description: "caret at the start");
        await auto.Shift().KeyAsync(Hex1bKey.DownArrow, ct: ct);
        await auto.WaitUntilAsync(s => AppTest.CaretAt(s, 7, 1), description: "the first line is selected");
        await auto.TypeAsync("x", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> x  nop" && !s.ContainsText("...>"), description: "the selection is replaced by the typed character");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Ctrl+C with a selection copies it through the terminal and leaves the buffer alone.
    /// </summary>
    [TestMethod]
    public async Task Selection_CtrlC_Copies()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var recorder = new PresentationRecorder();
        await using var terminal = AppTest.Build(engine, transcript, configure: b => b.AddPresentationFilter(recorder));
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method void F() {"], ct);
        await auto.TypeAsync("nop", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 1) == "  ...>   nop", description: "two lines");
        await auto.UpAsync(ct: ct);
        await auto.HomeAsync(ct: ct);
        await auto.Shift().KeyAsync(Hex1bKey.DownArrow, ct: ct);
        await auto.WaitUntilAsync(s => AppTest.CaretAt(s, 7, 1), description: "the first line is selected");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilAsync(_ => recorder.Output.Contains("\x1b]52;c;", StringComparison.Ordinal), description: "the terminal is asked to copy");
        var payload = recorder.Output[(recorder.Output.LastIndexOf("\x1b]52;c;", StringComparison.Ordinal) + 7)..];
        payload = payload[..payload.IndexOfAny(['\x07', '\x1b'])];
        var copied = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        Assert.AreEqual(".method void F() {\n", copied);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> .method void F() {" && AppTest.PromptRow(s, 1) == "  ...>   nop", description: "the buffer is untouched");
        Assert.IsFalse(run.IsCompleted, "Ctrl+C with a selection copies; it does not quit");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Up on an empty prompt recalls a block as one entry, with the caret on its last line.
    /// </summary>
    [TestMethod]
    public async Task History_RecallsMultiLineEntry()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_twice, ct);
        await auto.WaitUntilTextAsync("end of method Twice");
        await auto.WaitUntilTextAsync("il[2]>");
        await auto.WaitUntilNoTextAsync("editing");
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilTextAsync("editing 6 lines");
        await auto.WaitUntilAsync(s =>
        {
            var rows = s.FindText("il[2]> .method int32 Twice(int32 n) {");
            return rows.Count == 1 && AppTest.Row(s, rows[0].Line + 5) == "  ...> }" && AppTest.Caret(s) == (8, rows[0].Line + 5);
        }, description: "the whole block is back with the caret after its last brace");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Up and Down move through the buffer's lines and reach history only from its first and last line.
    /// </summary>
    [TestMethod]
    public async Task UpDown_MoveInsideBufferAndReachHistoryAtEdges()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["nop"], ct);
        await auto.WaitUntilTextAsync("il[1]> nop");
        await AppTest.TypeLinesAsync(auto, [".method void F() {", "ldc.i4 1"], ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 3 lines") && AppTest.CaretAt(s, 9, 2), description: "three lines with the caret on the third");

        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.CaretLine(s) == 1 && s.ContainsText("editing 3 lines"), description: "Up moves to the second line and recalls nothing");
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.CaretLine(s) == 0 && s.ContainsText("editing 3 lines"), description: "Up moves to the first line");
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> nop" && !s.ContainsText("editing"), description: "Up on the first line recalls the previous entry");
        await auto.DownAsync(ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 3 lines") && AppTest.PromptRow(s, 1) == "  ...>   ldc.i4 1", description: "Down on the last line goes forward to the draft");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Ctrl+P recalls from any line of the buffer.
    /// </summary>
    [TestMethod]
    public async Task CtrlP_RecallsFromTheMiddle()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["nop"], ct);
        await auto.WaitUntilTextAsync("il[1]> nop");
        await AppTest.TypeLinesAsync(auto, [".method void F() {", "ldc.i4 1"], ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 3 lines") && AppTest.CaretAt(s, 9, 2), description: "three lines");
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.CaretLine(s) == 1, description: "the caret is on the middle line");
        await auto.Ctrl().KeyAsync(Hex1bKey.P, ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> nop" && !s.ContainsText("editing"), description: "Ctrl+P recalls from the middle");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Tab on a blank continuation line indents it further.
    /// </summary>
    [TestMethod]
    public async Task Tab_IndentsBlankContinuationLine()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method void F() {"], ct);
        await auto.WaitUntilAsync(s => AppTest.CaretAt(s, 9, 1), description: "the continuation line is indented by one unit");
        await auto.TabAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.CaretAt(s, 11, 1) && AppTest.PromptRow(s, 1) == "  ...>", description: "Tab adds a unit");
        await auto.TypeAsync("nop", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 1) == "  ...>     nop", description: "the text follows the indentation");
        Assert.IsFalse(terminal.CreateSnapshot().ContainsText("opcodes"), "Tab on a blank line opens no palette");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Enter with the palette open but never navigated submits the typed text as it is.
    /// </summary>
    [TestMethod]
    public async Task Palette_EnterWithoutNavigation_Submits()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("ldc.i4.", ct: ct);
        await auto.WaitUntilTextAsync("opcodes 1/11");
        await auto.WaitUntilNoTextAsync("Enter accepts");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(_ => AppTest.Echoes(transcript).Contains("il[1]> ldc.i4.") && transcript.Lines.Any(l => l.Kind == LineKind.Error), description: "the text went to the engine, which refused it");

        // A refused line comes back selected, so typing replaces it; Ctrl+C on that selection clears it.
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0).StartsWith("il[1]> ldc.i4.", StringComparison.Ordinal) && s.GetCell(7, AppTest.PromptTop(s)).Background is not null, description: "the refused line is back and selected");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]>", description: "Ctrl+C clears the returned line in one press");
        Assert.IsFalse(run.IsCompleted);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Enter after moving the palette highlight accepts the highlighted opcode without submitting.
    /// </summary>
    [TestMethod]
    public async Task Palette_EnterAfterDown_AcceptsWithoutSubmit()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("ldc.i4.", ct: ct);
        await auto.WaitUntilTextAsync("opcodes 1/11");
        await auto.DownAsync(ct: ct);
        await auto.WaitUntilTextAsync("❯ ldc.i4.1");
        await auto.WaitUntilTextAsync("Enter accepts");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> ldc.i4.1" && !s.ContainsText("opcodes"), description: "the opcode is in the buffer and the palette is closed");
        Assert.IsEmpty(AppTest.Echoes(transcript), "accepting a completion sends nothing");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32]");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// The editor grows with the buffer up to a third of the screen, then scrolls inside that.
    /// </summary>
    [TestMethod]
    public async Task Editor_GrowsThenCapsAtThirdOfHeight()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript, 100, 30);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method void F() {", "nop", "nop"], ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 4 lines") && PromptRows(s) == 4, description: "four lines take four rows");
        await AppTest.TypeLinesAsync(auto, Enumerable.Repeat("nop", 8), ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 12 lines") && PromptRows(s) == 10 && AppTest.CaretAt(s, 9, 9), description: "twelve lines take ten rows, a third of thirty, with the caret on the last");
        Assert.IsFalse(terminal.CreateSnapshot().ContainsText("il[1]>"), "the first line has scrolled out of the editor");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// The echoed line is lit like the buffer: the opcode wears the opcode colour.
    /// </summary>
    [TestMethod]
    public async Task EchoLine_UsesOpcodeColor()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["nop"], ct);
        await auto.WaitUntilAsync(s => s.FindText("il[1]> nop") is [var hit] && Equals(s.GetCell(hit.Column + 7, hit.Line).Foreground, SpanPalette.Color(SpanStyle.Opcode)), description: "the echoed opcode is in the opcode colour");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// The buffer is lit before Enter: a known opcode wears the opcode colour as it is typed.
    /// </summary>
    [TestMethod]
    public async Task Buffer_UsesOpcodeColorBeforeEnter()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("nop", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> nop" && Equals(s.GetCell(7, AppTest.PromptTop(s)).Foreground, SpanPalette.Color(SpanStyle.Opcode)) && (s.GetCell(7, AppTest.PromptTop(s)).Attributes & CellAttributes.Underline) == 0, description: "the typed opcode is in the opcode colour");
        Assert.IsEmpty(AppTest.Echoes(transcript));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A first word the engine does not know is underlined before Enter.
    /// </summary>
    [TestMethod]
    public async Task Buffer_UnknownFirstWord_IsUnderlined()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("nopx ", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> nopx" && (s.GetCell(7, AppTest.PromptTop(s)).Attributes & CellAttributes.Underline) != 0 && (s.GetCell(10, AppTest.PromptTop(s)).Attributes & CellAttributes.Underline) != 0 && (s.GetCell(11, AppTest.PromptTop(s)).Attributes & CellAttributes.Underline) == 0, description: "the unknown word is underlined");
        Assert.IsEmpty(AppTest.Echoes(transcript));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Ctrl+C clears a buffer that has text and quits when the buffer is empty.
    /// </summary>
    [TestMethod]
    public async Task CtrlC_ClearsBufferThenQuits()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript, 120, 30);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method void F() {"], ct);
        await auto.TypeAsync("nop", ct: ct);
        await auto.WaitUntilTextAsync("editing 2 lines");
        await auto.WaitUntilTextAsync("Ctrl+C clears");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]>" && !s.ContainsText("editing") && !s.ContainsText("...>"), description: "the buffer is cleared");
        Assert.IsFalse(run.IsCompleted, "clearing does not quit");
        Assert.IsEmpty(AppTest.Echoes(transcript), "nothing went to the engine");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
        await run;
    }

    /// <summary>
    /// A history store that cannot save says so once, and editing goes on.
    /// </summary>
    [TestMethod]
    public async Task History_Problem_PrintsOneLine()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var store = new MemoryHistoryStore { Problem = "history.lock is held by another process" };
        await using var terminal = AppTest.Build(engine, transcript, history: store);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["nop"], ct);
        await auto.WaitUntilTextAsync("history is not being saved: history.lock is held by another process");
        await AppTest.TypeLinesAsync(auto, ["nop"], ct);
        await auto.WaitUntilAsync(_ => AppTest.Echoes(transcript).Count == 2, description: "the second line goes by");
        Assert.HasCount(1, transcript.Lines.Where(l => l.PlainText.Contains("history is not being saved", StringComparison.Ordinal)).ToList(), "the problem is reported once");
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.Row(s, s.Height - 3) == "il[1]> nop" || s.ContainsText("il[1]> nop"), description: "recall still works in memory");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    private static int PromptRows(Hex1bTerminalSnapshot s)
    {
        var rows = 0;
        for (var y = 0; y < s.Height; y++)
        {
            var row = s.GetLine(y);
            if (row.StartsWith("il[", StringComparison.Ordinal) || row.StartsWith("  ...> ", StringComparison.Ordinal))
            {
                rows++;
            }
        }

        return rows;
    }

    /// <summary>
    /// A method whose brace comes on the next line is one block: Enter continues from the header
    /// until the close, and the engine takes the brace as the header's own.
    /// </summary>
    [TestMethod]
    public async Task TypeMethod_BraceOnNextLine_SendsAtClose()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync(".method int32 One()", ct: ct);
        await auto.WaitUntilTextAsync("Enter continues");
        await auto.EnterAsync(ct: ct);
        await AppTest.TypeLinesAsync(auto, ["{", "ldc.i4 1", "ret"], ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 1) == "  ...> {" && AppTest.PromptRow(s, 2) == "  ...>   ldc.i4 1", description: "the brace opens the body's indentation");
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilTextAsync("Enter sends 5 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method One");
        await auto.WaitUntilTextAsync("il[2]>");
        Assert.DoesNotContain(l => l.Kind == LineKind.Error, transcript.Lines);
        Assert.AreEqual(0, engine.Status.OpenDepth);
        await AppTest.TypeLinesAsync(auto, ["call int32 One()", "ret"], ct);
        await auto.WaitUntilTextAsync("= 1 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A line submitted before the history store has answered stays recallable once it does.
    /// </summary>
    [TestMethod]
    public async Task History_SubmittedBeforeLoad_StaysRecallable()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var store = new MemoryHistoryStore { HoldLoad = new TaskCompletionSource() };
        store.Stored.Add("ldc.i4 1");
        await using var terminal = AppTest.Build(engine, transcript, history: store);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["nop"], ct);
        await auto.WaitUntilTextAsync("1 instruction");
        await auto.WaitUntilAsync(_ => store.Loads == 1, description: "the store was asked");
        store.HoldLoad.SetResult();

        // A keystroke and its frame follow the load, which the frame drains before it draws.
        await auto.TypeAsync("x", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> x", description: "a frame after the load");
        await auto.BackspaceAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]>", description: "empty again");
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> nop", description: "the line typed before the load is the newest entry");
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> ldc.i4 1", description: "the stored entry is before it");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A comment before the header does not change what the header is: the block waits for its
    /// brace, a refused body line brings the whole block back, and the correction commits it.
    /// </summary>
    [TestMethod]
    public async Task TypeMethod_CommentBeforeHeader_RecoversWhole()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("/* note */ .method int32 F()", ct: ct);
        await auto.WaitUntilTextAsync("Enter continues");
        await auto.EnterAsync(ct: ct);
        await AppTest.TypeLinesAsync(auto, ["{", "lcd.i4 1", "ret"], ct);
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilTextAsync("Enter sends 5 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("unknown opcode 'lcd.i4'");
        await auto.WaitUntilTextAsync("method F abandoned; the block is back in the editor");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 5 lines") && AppTest.PromptRow(s, 0) == "il[1]> /* note */ .method int32 F()" && AppTest.PromptRow(s, 2) == "  ...>   lcd.i4 1" && AppTest.CaretLine(s) == 2, description: "the whole block is back with the refused line selected");
        Assert.AreEqual(0, engine.Status.OpenDepth);
        Assert.IsNull(engine.Status.OpenMethod);
        await auto.TypeAsync("  ldc.i4 1", ct: ct);
        await auto.WaitUntilTextAsync("Enter sends 5 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method F");
        await AppTest.TypeLinesAsync(auto, ["call int32 F()", "ret"], ct);
        await auto.WaitUntilTextAsync("= 1 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A history load that lands while the user is browsing keeps the draft they left.
    /// </summary>
    [TestMethod]
    public async Task History_LoadWhileBrowsing_KeepsTheDraft()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var store = new MemoryHistoryStore { HoldLoad = new TaskCompletionSource() };
        store.Stored.Add("ldc.i4 1");
        await using var terminal = AppTest.Build(engine, transcript, history: store);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["nop"], ct);
        await auto.WaitUntilTextAsync("1 instruction");
        await auto.TypeAsync("ldc.i4 42", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> ldc.i4 42", description: "a draft in the buffer");
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> nop", description: "Up recalls the entry typed before the load");
        await auto.WaitUntilAsync(_ => store.Loads == 1, description: "the store was asked");
        store.HoldLoad.SetResult();

        // A keystroke and its frame follow the load, which the frame drains before it draws.
        await auto.EndAsync(ct: ct);
        await auto.TypeAsync("x", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> nopx", description: "a frame after the load");
        await auto.BackspaceAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> nop", description: "the entry again");
        await auto.DownAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> ldc.i4 42", description: "Down brings the draft back");
        await auto.UpAsync(ct: ct);
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> ldc.i4 1", description: "the stored entry is further back");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A space after the first word moves the caret into the operand: the palette closes and Up
    /// walks history again.
    /// </summary>
    [TestMethod]
    public async Task Palette_ClosesAfterOperandSpace()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["nop"], ct);
        await auto.WaitUntilTextAsync("il[1]> nop");
        await auto.TypeAsync("ldc.i4.", ct: ct);
        await auto.WaitUntilTextAsync("opcodes 1/11");
        await auto.TypeAsync(" ", ct: ct);
        await auto.WaitUntilNoTextAsync("opcodes");
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> nop", description: "Up recalls history rather than moving the palette");
        await auto.DownAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> ldc.i4.", description: "Down brings the draft back");
        Assert.IsFalse(terminal.CreateSnapshot().ContainsText("opcodes"), "the palette stays closed while the caret is past the word");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }
}
