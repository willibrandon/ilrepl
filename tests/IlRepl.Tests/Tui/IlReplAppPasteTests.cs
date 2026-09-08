using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// A bracketed paste lands in the buffer and waits for Enter; only the clipboard's own last
/// newline is dropped, so the buffer shows exactly the lines that will be sent.
/// </summary>
[TestClass]
public sealed class IlReplAppPasteTests
{
    /// <summary>
    /// The test context, for cancellation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A pasted block sits in the editor until Enter sends it.
    /// </summary>
    [TestMethod]
    public async Task PasteBlock_WaitsForEnter()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync(".method int32 Twice(int32 n) {\n  ldarg n\n  ldc.i4 2\n  mul\n  ret\n}\n");
        await auto.WaitUntilTextAsync("Enter sends 6 lines");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> .method int32 Twice(int32 n) {" && AppTest.PromptRow(s, 5) == "  ...> }" && AppTest.CaretAt(s, 8, 5), description: "the block is in the editor with the caret at its end");
        Assert.IsEmpty(AppTest.Echoes(transcript), "nothing is sent by the paste itself");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method Twice");
        Assert.HasCount(6, AppTest.Echoes(transcript));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Two cells separated by a blank line both run: the blank line is a run command.
    /// </summary>
    [TestMethod]
    public async Task Paste_TwoCellsSeparatedByBlank_RunsBoth()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync("ldc.i4.1\n\nldc.i4.2\n\n");
        await auto.WaitUntilTextAsync("Enter sends 4 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("il[3]>");
        var results = transcript.Lines.Where(l => l.Kind == LineKind.Result).Select(l => l.PlainText).ToList();
        Assert.HasCount(2, results);
        Assert.Contains("1", results[0]);
        Assert.Contains("2", results[1]);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// One final newline is the clipboard's terminator: the value stays on the stack.
    /// </summary>
    [TestMethod]
    public async Task Paste_OneTrailingNewline_LeavesValueOnStack()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync("ldc.i4.1\n");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 0) == "il[1]> ldc.i4.1" && !s.ContainsText("editing"), description: "one line, no blank line after it");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("stack [int32]");
        Assert.DoesNotContain(l => l.Kind == LineKind.Result, transcript.Lines);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Two final newlines are the terminator and a real blank line, which runs the cell.
    /// </summary>
    [TestMethod]
    public async Task Paste_TwoTrailingNewlines_RunsCell()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync("ldc.i4.1\n\n");
        await auto.WaitUntilTextAsync("Enter sends 2 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("il[2]>");
        Assert.HasCount(1, transcript.Lines.Where(l => l.Kind == LineKind.Result).ToList());

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A blank line inside a method is structure, not a run command, and is not sent.
    /// </summary>
    [TestMethod]
    public async Task Paste_BlankInsideMethod_NotSent()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync(".method int32 F() {\n  ldc.i4 1\n\n  ret\n}\n");
        await auto.WaitUntilTextAsync("Enter sends 5 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method F");
        Assert.AreSequenceEqual(["il[1]> .method int32 F() {", "il[1]>   ldc.i4 1", "il[1]>   ret", "il[1]> }"], AppTest.Echoes(transcript));
        Assert.DoesNotContain(l => l.Kind == LineKind.Error, transcript.Lines);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A comment line between cell lines is sent and ignored: the value stays on the stack.
    /// </summary>
    [TestMethod]
    public async Task Paste_CommentLineBetweenCells_DoesNotRun()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync("ldc.i4 1\n// note\n");
        await auto.WaitUntilTextAsync("Enter sends 2 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("il[1]> // note");
        await auto.WaitUntilTextAsync("stack [int32]");
        Assert.DoesNotContain(l => l.Kind == LineKind.Result, transcript.Lines);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A blank line inside a block comment is part of the comment, not a run command.
    /// </summary>
    [TestMethod]
    public async Task Paste_BlankLineInsideBlockComment_DoesNotRun()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync("ldc.i4 1\n/*\n\n*/\n");
        await auto.WaitUntilTextAsync("Enter sends 4 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("il[1]> */");
        await auto.WaitUntilTextAsync("stack [int32]");
        Assert.DoesNotContain(l => l.Kind == LineKind.Result, transcript.Lines);
        Assert.HasCount(4, AppTest.Echoes(transcript), "every line is echoed, the blank one included");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Enter on an empty buffer runs the pending cell, as it always has.
    /// </summary>
    [TestMethod]
    public async Task Enter_BlankBuffer_RunsCell()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["ldc.i4 1"], ct);
        await auto.WaitUntilTextAsync("stack [int32]");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("il[2]>");
        Assert.HasCount(1, transcript.Lines.Where(l => l.Kind == LineKind.Result).ToList());

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Pasted text keeps its own indentation; nothing is re-indented.
    /// </summary>
    [TestMethod]
    public async Task Paste_IndentedBlock_KeptAsIs()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync(".method void F() {\n      nop\n\tret\n}\n");
        await auto.WaitUntilTextAsync("Enter sends 4 lines");
        await auto.WaitUntilAsync(s => AppTest.PromptRow(s, 1) == "  ...>       nop" && AppTest.PromptRow(s, 3) == "  ...> }", description: "six spaces stay six spaces");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method F");
        Assert.AreSequenceEqual(["il[1]> .method void F() {", "il[1]>       nop", "il[1]> \tret", "il[1]> }"], AppTest.Echoes(transcript));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A pasted buffer that ends with a run line is recalled with that line, so Enter runs again.
    /// </summary>
    [TestMethod]
    public async Task Paste_TwoTrailingNewlines_RecallsWithTheRun()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);

        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync("ldc.i4.1\n\n");
        await auto.WaitUntilTextAsync("Enter sends 2 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("il[2]>");
        Assert.HasCount(1, transcript.Lines.Where(l => l.Kind == LineKind.Result).ToList());
        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("editing 2 lines") && AppTest.PromptRow(s, 0) == "il[2]> ldc.i4.1" && AppTest.PromptRow(s, 1) == "  ...>", description: "the entry comes back with its run line");
        await auto.WaitUntilTextAsync("Enter sends 2 lines");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("il[3]>");
        Assert.HasCount(2, transcript.Lines.Where(l => l.Kind == LineKind.Result).ToList(), "the recalled entry ran the cell again");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }
}
