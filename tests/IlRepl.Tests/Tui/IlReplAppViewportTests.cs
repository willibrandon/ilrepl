using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// The prompt keeps its caret in view on every frame, not only on the frame a wait happens to
/// see: the offsets are computed at render time from the caret, so the first frame after any
/// change is right.
/// </summary>
[TestClass]
public sealed class IlReplAppViewportTests
{
    private static readonly string[] s_long = [".method void F() {", .. Enumerable.Repeat("nop", 10), "ret", "}"];

    /// <summary>
    /// The test context, for cancellation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The very first frame shows the prompt with its caret cell.
    /// </summary>
    [TestMethod]
    public async Task Editor_InitialFrame_ShowsPromptAndCaret()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var recorder = new FrameRecorder();
        await using var terminal = AppTest.Build(engine, transcript, 80, 24, b => b.AddPresentationFilter(recorder));
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.WaitUntilAsync(_ => recorder.Count > 0, description: "a frame was recorded");
        var first = recorder.Frames[0];
        Assert.IsTrue(first.Contains("il[1]>"), first.ToString());
        Assert.AreEqual((7, 22), first.Caret, first.ToString());

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Typing past the editor's row cap scrolls the buffer so every frame shows the caret's line.
    /// </summary>
    [TestMethod]
    public async Task Typing_PastCap_EveryFrameShowsCaretLine()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var recorder = new FrameRecorder();
        await using var terminal = AppTest.Build(engine, transcript, 80, 24, b => b.AddPresentationFilter(recorder));
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        var start = recorder.Count;
        await AppTest.TypeLinesAsync(auto, s_long[..^1], ct);
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 13 lines") && AppTest.PromptRow(s, 7) == "  ...> }" && AppTest.CaretAt(s, 8, 7), description: "thirteen lines in eight rows with the caret on the last");
        AssertEveryFrameShowsCaret(recorder.Since(start));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Moving the caret up through a scrolled buffer keeps it in view on every frame, all the way
    /// to the first line.
    /// </summary>
    [TestMethod]
    public async Task CaretMove_MiddleOfDocument_EveryFrameShowsCaret()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var recorder = new FrameRecorder();
        await using var terminal = AppTest.Build(engine, transcript, 80, 24, b => b.AddPresentationFilter(recorder));
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_long[..^1], ct);
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 13 lines") && AppTest.CaretAt(s, 8, 7), description: "the caret is on the last line");
        var start = recorder.Count;
        for (var i = 0; i < 12; i++)
        {
            await auto.UpAsync(ct: ct);
        }

        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> .method void F() {" && AppTest.CaretLine(s) == 0, description: "the first line is in view with the caret on it");
        AssertEveryFrameShowsCaret(recorder.Since(start));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Recalling a long entry shows its last line and the caret at once.
    /// </summary>
    [TestMethod]
    public async Task Recall_LongEntry_EveryFrameShowsCaretLine()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var recorder = new FrameRecorder();
        await using var terminal = AppTest.Build(engine, transcript, 80, 24, b => b.AddPresentationFilter(recorder));
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_long, ct);
        await auto.WaitUntilTextAsync("end of method F");
        await auto.WaitUntilNoTextAsync("editing");
        var start = recorder.Count;
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 13 lines") && AppTest.PromptRow(s, 7) == "  ...> }" && AppTest.CaretAt(s, 8, 7), description: "the recalled block shows its last line with the caret");
        AssertEveryFrameShowsCaret(recorder.Since(start));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A pasted line wider than the screen scrolls sideways so the caret's column is in view.
    /// </summary>
    [TestMethod]
    public async Task Paste_WideLastLine_ShowsCaretColumn()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var recorder = new FrameRecorder();
        var adapter = new ScriptedPresentationAdapter(80, 24);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).AddPresentationFilter(recorder).Build();
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        var start = recorder.Count;
        var wide = "ldstr \"" + new string('x', 100) + "end\"";
        await adapter.PasteAsync("ldc.i4 1\n" + wide + "\n");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 2 lines") && AppTest.PromptRow(s, 1).EndsWith("xxxend\"", StringComparison.Ordinal) && AppTest.CaretAt(s, 79, 1), description: "the end of the wide line and the caret are in view");
        AssertEveryFrameShowsCaret(recorder.Since(start));
        await auto.HomeAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 1).StartsWith("  ...> ldstr \"xxx", StringComparison.Ordinal) && AppTest.CaretAt(s, 7, 1), description: "Home scrolls back to the start");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A refused line deep in a long block is scrolled into view and selected when the block comes back.
    /// </summary>
    [TestMethod]
    public async Task Refused_DeepLine_IsVisibleAndSelected()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var recorder = new FrameRecorder();
        await using var terminal = AppTest.Build(engine, transcript, 80, 24, b => b.AddPresentationFilter(recorder));
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        string[] block = [".method void F() {", .. Enumerable.Repeat("nop", 9), "lcd.i4 1", "ret", "}"];
        var start = recorder.Count;
        await AppTest.TypeLinesAsync(auto, block, ct);
        await auto.WaitUntilTextAsync("unknown opcode 'lcd.i4'");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 13 lines") && AppTest.Caret(s) is { } c && AppTest.Row(s, c.Y) == "  ...>   lcd.i4 1" && s.GetCell(9, c.Y).Background is not null, description: "the refused line is in view, selected, with the caret on it");
        AssertEveryFrameShowsCaret(recorder.Since(start));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Shrinking the terminal keeps the caret in view on every frame drawn at the new size.
    /// </summary>
    [TestMethod]
    public async Task Resize_Shrink_EveryFrameShowsCaret()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var recorder = new FrameRecorder();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).AddPresentationFilter(recorder).Build();
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_long[..^1], ct);
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 13 lines") && AppTest.CaretAt(s, 8, 9), description: "ten rows at thirty lines");
        var start = recorder.Count;
        adapter.Resize(60, 12);
        await auto.WaitUntilAsync(s => s.Width == 60 && s.Height == 12 && AppTest.PromptRow(s, 3) == "  ...> }" && AppTest.CaretAt(s, 8, 3), description: "four rows at twelve lines, the caret still on the last line");
        AssertEveryFrameShowsCaret(DrawnAt(recorder.Since(start), 60, 12));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Growing the terminal shows more of the buffer and keeps the caret in view on every frame
    /// drawn at the new size.
    /// </summary>
    [TestMethod]
    public async Task Resize_Grow_EveryFrameShowsCaret()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var recorder = new FrameRecorder();
        var adapter = new ScriptedPresentationAdapter(60, 12);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).AddPresentationFilter(recorder).Build();
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, s_long[..^1], ct);
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 13 lines") && AppTest.CaretAt(s, 8, 3), description: "four rows at twelve lines");
        var start = recorder.Count;
        adapter.Resize(100, 40);
        await auto.WaitUntilAsync(s => s.Width == 100 && s.Height == 40 && AppTest.PromptRow(s, 0) == "il[1]> .method void F() {" && AppTest.CaretAt(s, 8, 12), description: "all thirteen lines fit and the caret is on the last");
        AssertEveryFrameShowsCaret(DrawnAt(recorder.Since(start), 100, 40));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// The gutter stays aligned beside lines with accented characters and tabs.
    /// </summary>
    [TestMethod]
    public async Task Gutter_UnicodeAndTabs_AlignWithText()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(80, 24);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync(".method void F() {\n  ldstr \"héllo wörld\"\n\tpop\n  ret\n}");
        await auto.WaitUntilAsync(s => s.ContainsText("editing 5 lines") && AppTest.PromptRow(s, 1) == "  ...>   ldstr \"héllo wörld\"" && AppTest.PromptRow(s, 2).EndsWith("pop", StringComparison.Ordinal) && AppTest.CaretAt(s, 8, 4), description: "every line is beside its gutter");
        using var snapshot = terminal.CreateSnapshot();
        var top = AppTest.PromptTop(snapshot);
        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual(">", snapshot.GetCell(5, top + i).Character, $"row {i}");
            Assert.AreEqual(" ", snapshot.GetCell(6, top + i).Character, $"row {i}");
        }

        Assert.AreEqual("\"", snapshot.GetCell(7 + 8, top + 1).Character, "the accented characters are one column each");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method F");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// On a tiny terminal the transcript, separator, editor, and status bar each keep their rows.
    /// </summary>
    [TestMethod]
    public async Task SmallTerminal_10x40_NothingOverlaps()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var recorder = new FrameRecorder();
        await using var terminal = AppTest.Build(engine, transcript, 40, 10, b => b.AddPresentationFilter(recorder));
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["nop"], ct);
        await auto.WaitUntilTextAsync("il[1]> nop");
        var start = recorder.Count;
        await AppTest.TypeLinesAsync(auto, [".method int32 Twice(int32 n) {", "ldarg n", "ldc.i4 2", "mul", "ret"], ct);
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilAsync(s => s.GetLine(9).Contains("editing 6 lines", StringComparison.Ordinal) && AppTest.PromptTop(s) == 6 && s.GetLine(5).StartsWith("───", StringComparison.Ordinal) && AppTest.CaretAt(s, 8, 2), description: "three editor rows above the status bar, the separator above them");
        using var snapshot = terminal.CreateSnapshot();
        Assert.IsTrue(snapshot.GetLine(1).StartsWith("il[1]> nop", StringComparison.Ordinal) || snapshot.GetLine(0).Contains("il[1]> nop", StringComparison.Ordinal) || snapshot.GetLine(2).StartsWith("il[1]> nop", StringComparison.Ordinal), "the transcript keeps the rows above the separator:\n" + snapshot.GetText());
        Assert.IsFalse(snapshot.ContainsText("opcodes"), "no palette on a small screen");
        Assert.IsTrue(snapshot.GetLine(9).Contains("Enter sends 6 lines", StringComparison.Ordinal), "what Enter does is the hint that fits: " + snapshot.GetLine(9));
        AssertEveryFrameShowsCaret(recorder.Since(start));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A click on a scrolled buffer lands on the line the row shows.
    /// </summary>
    [TestMethod]
    public async Task Click_OnScrolledBuffer_PlacesCaret()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript, 80, 24, b => b.WithMouse());
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        string[] block = [".method void F() {", .. Enumerable.Range(1, 10).Select(i => $"ldc.i4 {i}"), "ret"];
        await AppTest.TypeLinesAsync(auto, block, ct);
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 13 lines") && AppTest.PromptRow(s, 0) == "  ...>   ldc.i4 5" && AppTest.CaretAt(s, 8, 7), description: "the buffer is scrolled to its last eight lines");
        using (var before = terminal.CreateSnapshot())
        {
            await auto.ClickAtAsync(14, AppTest.PromptTop(before) + 2, ct: ct);
        }

        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "  ...>   ldc.i4 5" && AppTest.CaretAt(s, 14, 2) && AppTest.PromptRow(s, 2) == "  ...>   ldc.i4 7", description: "the caret is on the seventh value, where the click landed, and nothing scrolled");
        await auto.TypeAsync("0", ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 2) == "  ...>   ldc.i04 7", description: "typing goes where the click put the caret");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    // The terminal takes its new size before the app has drawn for it, so a frame in between can
    // be the old layout cropped. A frame the app drew at the new size has the status bar, with
    // the hint that names what Enter does, on the new last row.
    private static List<Frame> DrawnAt(IReadOnlyList<Frame> frames, int width, int height) =>
        frames.Where(f => f.Width == width && f.Height == height && f.Lines[^1].TrimEnd().EndsWith("lines", StringComparison.Ordinal)).ToList();

    private static void AssertEveryFrameShowsCaret(IReadOnlyList<Frame> frames)
    {
        Assert.IsNotEmpty(frames, "frames were rendered after the action");
        foreach (var frame in frames)
        {
            Assert.IsNotNull(frame.Caret, "a frame without the caret in view:\n" + frame);
        }
    }

    /// <summary>
    /// A line of wide characters scrolls by cells, not characters, so the caret's cell is in view.
    /// </summary>
    [TestMethod]
    public async Task Paste_WideCharacters_ShowsCaretColumn()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(80, 24);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync("ldstr \"" + new string('漢', 45) + "\"");
        await auto.WaitUntilAsync(s => AppTest.Caret(s) is { } c && c.Y == AppTest.PromptTop(s) && c.X > 7 && s.GetCell(c.X - 1, c.Y).Character == "\"", description: "the caret cell follows the closing quote, in view");
        await auto.HomeAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0).StartsWith("il[1]> ldstr \"漢", StringComparison.Ordinal) && AppTest.CaretAt(s, 7, 0), description: "Home scrolls back to the start");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A line of emoji scrolls by whole characters: nothing renders as a replacement glyph, the
    /// caret's cell is in view, and a click on either cell of an emoji puts the caret before it.
    /// </summary>
    [TestMethod]
    public async Task Paste_Emoji_ScrollsWholeCharactersAndClicksLand()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(80, 24);
        PromptState? prompt = null;
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript, onPrompt: p => prompt = p).WithPresentation(adapter).WithMouse().Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        var text = "ldstr \"" + string.Concat(Enumerable.Repeat("😀", 40)) + "\"";
        await adapter.PasteAsync(text);
        await auto.WaitUntilAsync(s => AppTest.Caret(s) is { } c && c.Y == AppTest.PromptTop(s) && s.GetCell(c.X - 1, c.Y).Character == "\"" && s.GetCell(7, c.Y).Character == "😀", description: "the row starts on a whole emoji and the caret follows the quote");
        Assert.DoesNotContain("\uFFFD", terminal.CreateSnapshot().GetText(), "no replacement glyph");

        // The last emoji takes the two cells before the quote; a click on either puts the caret
        // before it, at the character index where that emoji starts.
        var lastEmoji = text.Length - 3;
        foreach (var back in new[] { 3, 2 })
        {
            int x, y;
            using (var before = terminal.CreateSnapshot())
            {
                var caret = AppTest.Caret(before)!.Value;
                (x, y) = (caret.X - back, caret.Y);
            }

            await auto.ClickAtAsync(x, y, ct: ct);
            await auto.WaitUntilAsync(s => prompt!.Editor.Cursor.Position.Value == lastEmoji && AppTest.Caret(s) is { } c && c.Y == y && s.GetCell(c.X, c.Y).Character == "😀" && s.GetCell(c.X + 2, c.Y).Character == "\"", description: "the caret sits on the last emoji's first cell");
            await auto.EndAsync(ct: ct);
            await auto.WaitUntilAsync(s => prompt!.Editor.Cursor.Position.Value == text.Length && AppTest.Caret(s) is { } c && s.GetCell(c.X - 1, c.Y).Character == "\"", description: "End returns the caret, on screen too, to the end of the line");
        }

        Assert.DoesNotContain("\uFFFD", terminal.CreateSnapshot().GetText(), "still no replacement glyph");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// The scrolled left edge is a character index chosen for the caret's line; on another line
    /// it may fall inside an emoji. Each row starts on a whole character of its own, and a click
    /// on that row lands on the line it shows.
    /// </summary>
    [TestMethod]
    public async Task Paste_EmojiOnAnotherLine_StartsOnWholeCharacters()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(80, 24);
        PromptState? prompt = null;
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript, onPrompt: p => prompt = p).WithPresentation(adapter).WithMouse().Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        // The second line is one column wider than the text area, so the left edge moves to
        // character index one, which on the first line is inside the emoji.
        await adapter.PasteAsync("😀 nop\n" + new string('x', 73));
        await auto.WaitUntilAsync(s => AppTest.CaretLine(s) == 1 && AppTest.PromptRow(s, 0).StartsWith("il[1]> 😀 nop", StringComparison.Ordinal) && s.GetCell(7, AppTest.PromptTop(s)).Character == "😀", description: "the first row starts on the whole emoji while the caret's line is scrolled");
        Assert.DoesNotContain("\uFFFD", terminal.CreateSnapshot().GetText(), "no replacement glyph");

        int top;
        using (var before = terminal.CreateSnapshot())
        {
            top = AppTest.PromptTop(before);
        }

        await auto.ClickAtAsync(7, top, ct: ct);
        await auto.WaitUntilAsync(_ => prompt!.CaretLine == 1 && prompt.Editor.Cursor.Position.Value <= 2, description: "a click on the emoji's row puts the caret on that line, at the emoji");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A line of decomposed accents is many characters but few cells: it fits, so the whole line
    /// stays in view with the opcode at the left and the caret after the quote.
    /// </summary>
    [TestMethod]
    public async Task Paste_DecomposedAccents_StaysFullyVisible()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(80, 24);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync("ldstr \"" + string.Concat(Enumerable.Repeat("e\u0301", 60)) + "\"");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0).StartsWith("il[1]> ldstr \"e", StringComparison.Ordinal) && AppTest.Caret(s) is { } c && c.Y == AppTest.PromptTop(s) && s.GetCell(c.X - 1, c.Y).Character == "\"" && c.X == 7 + 68, description: "the whole line is in view with the caret after the quote");
        await auto.HomeAsync(ct: ct);
        await auto.WaitUntilAsync(s => AppTest.CaretAt(s, 7, 0) && AppTest.PromptRow(s, 0).StartsWith("il[1]> ldstr", StringComparison.Ordinal), description: "Home keeps the line where it is");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A line of joined emoji scrolls by whole joined characters and never shows a replacement glyph.
    /// </summary>
    [TestMethod]
    public async Task Paste_JoinedEmoji_ScrollsWholeCharacters()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(80, 24);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync("ldstr \"" + string.Concat(Enumerable.Repeat("👩\u200D💻", 40)) + "\"");
        await auto.WaitUntilAsync(s => AppTest.Caret(s) is { } c && c.Y == AppTest.PromptTop(s) && s.GetCell(c.X - 1, c.Y).Character == "\"" && s.GetCell(7, c.Y).Character.StartsWith("👩", StringComparison.Ordinal), description: "the row starts on a whole joined emoji and the caret follows the quote");
        Assert.DoesNotContain("\uFFFD", terminal.CreateSnapshot().GetText(), "no replacement glyph");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }
}
