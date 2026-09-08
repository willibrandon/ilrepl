using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Drives the real terminal UI on the Hex1b headless emulator with a real host process behind it.
/// </summary>
[TestClass]
public sealed class IlReplAppTests
{
    /// <summary>
    /// The test context, for cancellation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Typing instructions echoes the stack, and ret prints the value.
    /// </summary>
    /// <param name="width">The terminal width.</param>
    /// <param name="height">The terminal height.</param>
    [TestMethod]
    [DataRow(80, 24)]
    [DataRow(120, 40)]
    public async Task TypeInstructions_ShowsStackAndResult(int width, int height)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(width, height)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("ldc.i4 6", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32]");
        await auto.TypeAsync("ldc.i4 7", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32, int32]");
        await auto.TypeAsync("mul", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("stack [int32]"), description: "status bar shows one value");
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 42 : int32");
        await auto.WaitUntilTextAsync("il[2]>");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// The completion palette opens on a prefix, Escape dismisses it, typing reopens it, the arrows
    /// move the highlight, and Tab accepts the highlighted opcode.
    /// </summary>
    [TestMethod]
    public async Task Palette_DismissReopenSelectAndAccept()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("ldc.i4.", ct: ct);
        await auto.WaitUntilTextAsync("opcodes 1/11");
        await auto.WaitUntilTextAsync("❯ ldc.i4.0");
        await auto.EscapeAsync(ct: ct);
        await auto.WaitUntilNoTextAsync("opcodes 1/11");

        // An exact opcode has nothing to complete, so the palette stays closed.
        await auto.TypeAsync("s", ct: ct);
        await auto.WaitUntilTextAsync("il[1]> ldc.i4.s");
        await auto.WaitUntilNoTextAsync("opcodes");

        // Editing the word reopens it; the arrows move the highlight; Tab accepts.
        await auto.BackspaceAsync(ct: ct);
        await auto.WaitUntilTextAsync("opcodes 1/11");
        await auto.DownAsync(ct: ct);
        await auto.DownAsync(ct: ct);
        await auto.WaitUntilTextAsync("opcodes 3/11");
        await auto.WaitUntilTextAsync("❯ ldc.i4.2");
        await auto.TabAsync(ct: ct);
        await auto.WaitUntilNoTextAsync("opcodes 3/11");
        await auto.WaitUntilTextAsync("il[1]> ldc.i4.2");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32]");
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 2 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Up recalls the previous line and errors show in red.
    /// </summary>
    [TestMethod]
    public async Task History_And_ErrorColor()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("lcd.i4 1", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("did you mean 'ldc.i4'");

        using (var snapshot = auto.CreateSnapshot())
        {
            Assert.IsTrue(snapshot.HasForegroundColor(SpanPalette.Color(SpanStyle.Error)), "the error label should use the error color");
        }

        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("il[1]> lcd.i4 1"), description: "history recalled into the prompt");
        await auto.HomeAsync(ct: ct);
        await auto.DeleteAsync(ct: ct);
        await auto.DeleteAsync(ct: ct);
        await auto.DeleteAsync(ct: ct);
        await auto.TypeAsync("ldc", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32]");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A mouse click on the transcript does not take keyboard focus away from the prompt.
    /// </summary>
    [TestMethod]
    public async Task ClickOnTranscript_KeepsTypingAtThePrompt()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .WithMouse()
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.ClickAtAsync(40, 10, ct: ct);
        await auto.TypeAsync("ldc.i4 5", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32]");
        await auto.ClickAtAsync(20, 5, ct: ct);
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 5 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// The palette's columns stay put while the selection scrolls through the whole list, so the
    /// descriptions do not jump left and right.
    /// </summary>
    [TestMethod]
    public async Task Palette_ColumnsStayPutWhileScrolling()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        var commands = engine.Catalog.Where(c => c.Name.StartsWith('.')).ToList();
        await auto.TypeAsync(".", ct: ct);
        await auto.WaitUntilTextAsync($"commands 1/{commands.Count}");

        var descriptionColumns = new HashSet<int>();
        for (var step = 0; step < commands.Count; step++)
        {
            await auto.WaitUntilTextAsync($"commands {step + 1}/{commands.Count}");
            string[] lines;
            using (var snapshot = auto.CreateSnapshot())
            {
                lines = snapshot.GetScreenText().Split('\n');
            }

            foreach (var item in commands)
            {
                var row = Array.Find(lines, line => line.Contains(' ' + item.Name + ' ', StringComparison.Ordinal) && line.Contains(item.Description, StringComparison.Ordinal));
                if (row is not null)
                {
                    descriptionColumns.Add(row.IndexOf(item.Description, StringComparison.Ordinal));
                }
            }

            await auto.DownAsync(ct: ct);
        }

        Assert.HasCount(1, descriptionColumns, "descriptions should start in the same column on every row at every scroll position");

        await auto.EscapeAsync(ct: ct);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Long transcript lines wrap instead of being cut off, so the help is readable in a narrow
    /// terminal. A description after a label folds under itself, at the label's width.
    /// </summary>
    [TestMethod]
    public async Task Help_WrapsInNarrowTerminal()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(60, 124)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync(".help", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("Ctrl+Q leaves.");

        string[] rows;
        using (var snapshot = auto.CreateSnapshot())
        {
            // The scrollbar occupies the last column; its glyphs are not part of the text.
            rows = snapshot.GetScreenText().Split('\n').Select(r => r.TrimEnd().TrimEnd('▉', '│').TrimEnd()).ToArray();
        }

        // Folded rows read back as one paragraph once the line breaks are folded away.
        var text = string.Join(' ', rows.Select(r => r.Trim()).Where(r => r.Length > 0));
        Assert.Contains("ret or an empty line compiles the cell, runs it, and prints the value left on the stack.", text, "the paragraph should be readable in full");
        Assert.Contains("cell arguments and the values passed each run", text, "a description should be readable in full");

        // Rows fill the width beside the scrollbar; they are not folded early.
        Assert.Contains("Type one IL instruction per line. The simulated stack is", rows.Select(r => r.TrimEnd()), "the first row of the paragraph should use the full width");
        Assert.Contains("shown after each one.", rows.Select(r => r.TrimEnd()));
        Assert.Contains(r => r.StartsWith("  ldc.i4 6 ", StringComparison.Ordinal), rows, "an indented example should keep its indentation");
        Assert.Contains(r => r.StartsWith("  .args (T name = literal, ...)", StringComparison.Ordinal), rows, "an entry should keep its indentation and label");
        Assert.Contains(r => r.StartsWith(new string(' ', 32), StringComparison.Ordinal) && r.Trim().Length > 0, rows, "a folded description should continue at the label's width");
        var separator = Array.FindIndex(rows, r => r.StartsWith("──", StringComparison.Ordinal));
        Assert.IsGreaterThan(0, separator, "the separator above the prompt should be on screen");
        Assert.DoesNotContain(r => r.TrimEnd().Length > 59, rows.Take(separator), "no transcript row should run into the scrollbar column");

        // At this width only the last hint fits beside the facts; it is never clipped.
        Assert.EndsWith("Ctrl+Q quit", rows[^1].TrimEnd(), "the quit hint should be whole");
        Assert.DoesNotContain("Tab complete", rows[^1], "a hint that does not fit should be dropped");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Dragging across the transcript selects text, the status bar switches to copy mode's keys,
    /// y copies to the clipboard through the terminal, the selection is gone, the status bar says
    /// what was yanked, and the prompt is back in charge. The caret the terminal is asked for is
    /// a blinking block.
    /// </summary>
    [TestMethod]
    public async Task DragOnTranscript_CopiesSelection()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        var recorder = new PresentationRecorder();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .AddPresentationFilter(recorder)
            .WithHeadless()
            .WithDimensions(100, 30)
            .WithMouse()
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.WaitUntilTextAsync("Tab complete │ Shift+↑ select │ Ctrl+Q quit");
        await auto.TypeAsync("ldc.i4 6", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32]");

        // The echoed line sits on the second row. Select it with the mouse, then yank with y.
        await auto.DragAsync(0, 1, 14, 1, ct: ct);
        await auto.WaitUntilTextAsync("Shift+↑↓ extend │ y yank │ Esc cancel");
        await auto.TypeAsync("y", ct: ct);
        await auto.WaitUntilAsync(_ => recorder.Output.Contains("\x1b]52;c;", StringComparison.Ordinal));
        var payload = recorder.Output[(recorder.Output.LastIndexOf("\x1b]52;c;", StringComparison.Ordinal) + 7)..];
        payload = payload[..payload.IndexOfAny(['\x07', '\x1b'])];
        var copied = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        Assert.Contains("ldc.i4 6", copied, "the selected row should be what was copied");

        // The yank is confirmed on the status bar, the selection is gone, and the message clears.
        await auto.WaitUntilTextAsync("Yanked: il[1]> ldc.i4 6");
        await auto.WaitUntilNoTextAsync("y yank");
        await auto.WaitUntilNoTextAsync("Yanked:", timeout: TimeSpan.FromSeconds(5));

        // The prompt paints its own caret cell in the prompt colour; no caret shape is ever asked for.
        Assert.DoesNotContain("\x1b[1 q", recorder.Output, "no caret shape should reach the terminal");
        Assert.DoesNotContain("\x1b[5 q", recorder.Output, "no caret shape should reach the terminal");
        Assert.DoesNotContain("\x1b[6 q", recorder.Output, "no caret shape should reach the terminal");
        Assert.Contains("48;2;97;175;239", recorder.Output, "the caret cell should be painted in the prompt colour");

        // Copy mode has ended; typing goes to the prompt again.
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 6 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A click ends a transcript selection as Escape does, whether it lands on the transcript or
    /// on the prompt, and so does typing; the prompt is in charge again at once and a later drag
    /// selects afresh.
    /// </summary>
    [TestMethod]
    public async Task Click_EndsTranscriptSelection()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript, configure: b => b.WithMouse());
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["ldc.i4 6"], ct);
        await auto.WaitUntilTextAsync("[int32]");

        // A drag selects the echoed line on the second row; a click on the transcript ends it.
        await auto.DragAsync(0, 1, 14, 1, ct: ct);
        await auto.WaitUntilTextAsync("y yank");
        await auto.ClickAtAsync(5, 1, ct: ct);
        await auto.WaitUntilNoTextAsync("y yank");

        // Selected again, a click on the prompt ends it as well.
        await auto.DragAsync(0, 1, 14, 1, ct: ct);
        await auto.WaitUntilTextAsync("y yank");
        var promptRow = -1;
        await auto.WaitUntilAsync(s => (promptRow = AppTest.PromptTop(s)) >= 0, description: "the prompt row");
        await auto.ClickAtAsync(12, promptRow, ct: ct);
        await auto.WaitUntilNoTextAsync("y yank");

        // Selected from the keyboard, typing ends it and the text lands in the prompt.
        await auto.Shift().KeyAsync(Hex1bKey.UpArrow, ct: ct);
        await auto.WaitUntilTextAsync("y yank");
        await auto.TypeAsync("ret", ct: ct);
        await auto.WaitUntilNoTextAsync("y yank");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> ret", description: "the typed text is in the prompt");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 6 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Shift+Up at the prompt selects the last transcript line, another Shift+Up extends the
    /// selection upward, y yanks both lines, and F12 is not bound to anything.
    /// </summary>
    [TestMethod]
    public async Task ShiftUp_SelectsLinesFromTheKeyboard()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        var recorder = new PresentationRecorder();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .AddPresentationFilter(recorder)
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("ldc.i4 6", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32]");

        await auto.Shift().KeyAsync(Hex1bKey.UpArrow, ct: ct);
        await auto.WaitUntilTextAsync("y yank");
        await auto.Shift().KeyAsync(Hex1bKey.UpArrow, ct: ct);
        await auto.TypeAsync("y", ct: ct);
        await auto.WaitUntilAsync(_ => recorder.Output.Contains("\x1b]52;c;", StringComparison.Ordinal));
        var payload = recorder.Output[(recorder.Output.LastIndexOf("\x1b]52;c;", StringComparison.Ordinal) + 7)..];
        payload = payload[..payload.IndexOfAny(['\x07', '\x1b'])];
        var copied = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        Assert.Contains("ldc.i4 6", copied, "the echoed line should be in the yank");
        Assert.Contains("[int32]", copied, "the stack line should be in the yank");
        await auto.WaitUntilTextAsync("Yanked 2 lines");

        // F12 does nothing, so the line after it goes to the prompt.
        await auto.KeyAsync(Hex1bKey.F12, ct: ct);
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 6 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// The transcript's scrollbar can be dragged with the mouse.
    /// </summary>
    [TestMethod]
    public async Task Scrollbar_DragScrollsTranscript()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .WithMouse()
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync(".help", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("Ctrl+Q leaves.");

        // The transcript follows its end, so the thumb sits at the bottom of the last column.
        await auto.DragAsync(99, 24, 99, 1, ct: ct);
        await auto.WaitUntilNoTextAsync("Ctrl+Q leaves.");
        await auto.WaitUntilTextAsync("Type one IL instruction");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Wheel reports scroll the transcript even when the app has not asked the terminal for mouse
    /// tracking.
    /// </summary>
    [TestMethod]
    public async Task Wheel_ScrollsTranscriptWithoutMouseTracking()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync(".help", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("Ctrl+Q leaves.");
        await auto.WaitUntilNoTextAsync("Type one IL instruction");

        await auto.MouseMoveToAsync(20, 5, ct: ct);
        await auto.ScrollUpAsync(20, ct: ct);
        await auto.WaitUntilTextAsync("Type one IL instruction");
        await auto.WaitUntilNoTextAsync("Ctrl+Q leaves.");

        await auto.ScrollDownAsync(20, ct: ct);
        await auto.WaitUntilTextAsync("Ctrl+Q leaves.");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Ctrl+L clears the transcript back to the banner.
    /// </summary>
    [TestMethod]
    public async Task CtrlL_ClearsTranscript()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("nop", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("il[1]> nop");
        await auto.Ctrl().KeyAsync(Hex1bKey.L, ct: ct);
        await auto.WaitUntilNoTextAsync("il[1]> nop");
        await auto.WaitUntilTextAsync(IlReplApp.Banner[..20]);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A method block stays in the editor until its closing brace: Enter continues it with the
    /// next line indented, the status bar counts the lines, and nothing reaches the engine. The
    /// brace sends the block line by line, every line keeps its echo and its stack line, the
    /// method fact shows only while the engine has it open, and the method is callable after.
    /// </summary>
    [TestMethod]
    public async Task TypeMethod_EnterContinuesAndCloseSubmits()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync(".method int32 Twice(int32 n) {", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("  ...> ");
        await auto.WaitUntilTextAsync("editing 2 lines");
        await auto.WaitUntilTextAsync("Enter continues");
        Assert.DoesNotContain(l => l.PlainText.Contains("Twice", StringComparison.Ordinal), transcript.Lines, "nothing goes to the engine before the block closes");

        foreach (var line in new[] { "ldarg n", "ldc.i4 2", "mul", "ret" })
        {
            await auto.TypeAsync(line, ct: ct);
            await auto.EnterAsync(ct: ct);
        }

        await auto.WaitUntilTextAsync("editing 6 lines");
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilTextAsync("Enter sends 6 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method Twice");
        await auto.WaitUntilTextAsync("il[2]>");
        await auto.WaitUntilNoTextAsync("method Twice │");
        await auto.WaitUntilNoTextAsync("editing");

        // Every line went by in order, each with its echo and its stack line.
        var echoes = transcript.Lines.Where(l => l.Kind == LineKind.Input).Select(l => l.PlainText).ToList();
        Assert.AreSequenceEqual(["il[1]> .method int32 Twice(int32 n) {", "il[1]>   ldarg n", "il[1]>   ldc.i4 2", "il[1]>   mul", "il[1]>   ret", "il[1]> }"], echoes);
        var afterLdarg = transcript.Lines.SkipWhile(l => l.PlainText != "il[1]>   ldarg n").Skip(1).First();
        Assert.AreEqual(LineKind.Stack, afterLdarg.Kind);
        Assert.Contains("[int32]", afterLdarg.PlainText);

        await auto.TypeAsync("ldc.i4 21", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.TypeAsync("call int32 Twice(int32)", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("stack [int32]"), description: "status bar shows the call's result type");
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 42 : int32");
        await auto.WaitUntilTextAsync("il[3]>");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A class with a method inside is one block: the nested braces indent as they open, the
    /// block stays in the editor until the outermost brace, and then it goes line by line, the
    /// class ahead of its method.
    /// </summary>
    [TestMethod]
    public async Task TypeClass_NestedBlockIsOneSubmission()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(120, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync(".class public Counter {", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.TypeAsync(".field public static int32 Count", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.TypeAsync(".method public static int32 Next() {", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("editing 4 lines");
        Assert.IsEmpty(transcript.Lines.Where(l => l.PlainText.Contains("Counter", StringComparison.Ordinal)), "nothing goes to the engine before the block closes");
        foreach (var line in new[] { "ldsfld int32 Counter::Count", "ret", "}" })
        {
            await auto.TypeAsync(line, ct: ct);
            await auto.EnterAsync(ct: ct);
        }

        await auto.WaitUntilTextAsync("editing 7 lines");
        await auto.WaitUntilTextAsync("Enter continues");
        await auto.TypeAsync("}", ct: ct);
        await auto.WaitUntilTextAsync("Enter sends 7 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method Next");
        await auto.WaitUntilTextAsync("end of class Counter");
        await auto.WaitUntilTextAsync("il[2]>");
        await auto.WaitUntilNoTextAsync("class Counter │");

        var echoes = transcript.Lines.Where(l => l.Kind == LineKind.Input).Select(l => l.PlainText).ToList();
        Assert.AreSequenceEqual(["il[1]> .class public Counter {", "il[1]>   .field public static int32 Count", "il[1]>   .method public static int32 Next() {", "il[1]>     ldsfld int32 Counter::Count", "il[1]>     ret", "il[1]>   }", "il[1]> }"], echoes);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// The palette offers .method ahead of .methods.
    /// </summary>
    [TestMethod]
    public async Task Palette_MethodPrefix_ShowsDirectiveAndCommand()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync(".me", ct: ct);
        await auto.WaitUntilTextAsync("❯ .method");
        await auto.WaitUntilTextAsync("T Name(T arg, ...) {");
        await auto.WaitUntilTextAsync("  .methods");
        using (var snapshot = auto.CreateSnapshot())
        {
            var rows = snapshot.GetScreenText().Split('\n');
            Assert.Contains(r => r.Contains("commands", StringComparison.Ordinal), rows, "the palette should be titled commands");
            var method = Array.FindIndex(rows, r => r.Contains("❯ .method", StringComparison.Ordinal));
            var methods = Array.FindIndex(rows, r => r.Contains("  .methods", StringComparison.Ordinal));
            Assert.IsGreaterThan(method, methods, ".method should be listed before .methods");
        }

        await auto.EscapeAsync(ct: ct);
        await auto.WaitUntilNoTextAsync("❯ .method");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }
}
