using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Checks the rendered help page's colors, spacing, actions, and footer through the real terminal and host.
/// </summary>
[TestClass]
public sealed class PromptHelpAppearanceTests
{
    /// <summary>
    /// Cancels terminal input and host operations when the test ends.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Short help keeps its dim Escape hint on the bottom row before and after a terminal resize.
    /// </summary>
    /// <param name="width">The initial terminal width.</param>
    /// <param name="height">The initial terminal height.</param>
    [TestMethod]
    [DataRow(100, 30)]
    [DataRow(50, 14)]
    public async Task InstructionHelp_PinsFooterAndUsesEditorPaletteAcrossResize(int width, int height)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        PromptState prompt = null!;
        var adapter = new ScriptedPresentationAdapter(width, height);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
            onPrompt: state => prompt = state).WithPresentation(adapter).Build();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = terminal.RunAsync(cancellation.Token);
        try
        {
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
            await auto.WaitUntilTextAsync("il[1]>");
            await auto.TypeAsync("nop", ct: ct);
            await auto.WaitUntilAsync(_ => prompt.Analysis is not null && prompt.Analyzer?.IsPending == false,
                description: "nop analysis is ready");
            await auto.KeyAsync(Hex1bKey.F1, ct: ct);
            await auto.WaitUntilAsync(snapshot => AppTest.Row(snapshot, 0) == "help · nop"
                && AppTest.Row(snapshot, height - 1).StartsWith("Esc back", StringComparison.Ordinal),
                description: "the complete help frame is visible");
            using (var snapshot = auto.CreateSnapshot())
            {
                AssertFrame(snapshot, width, height, "nop");
                AssertColor(snapshot, "nop", SpanStyle.Opcode, line: 2);
                AssertColor(snapshot, "Stack effect:", SpanStyle.Dim);
                AssertColor(snapshot, "Does nothing", SpanStyle.Default);
                Assert.AreEqual("", AppTest.Row(snapshot, height - 2), "Short help leaves space above the pinned footer.");
            }

            var resizedWidth = width == 100 ? 50 : 100;
            var resizedHeight = height == 30 ? 14 : 30;
            adapter.Resize(resizedWidth, resizedHeight);
            await auto.WaitUntilAsync(snapshot => snapshot.Width == resizedWidth && snapshot.Height == resizedHeight
                && AppTest.Row(snapshot, resizedHeight - 1).StartsWith("Esc back", StringComparison.Ordinal),
                description: "the footer follows the resized terminal");
            using (var snapshot = auto.CreateSnapshot())
            {
                AssertFrame(snapshot, resizedWidth, resizedHeight, "nop");
            }

            await auto.EscapeAsync(ct: ct);
            await auto.WaitUntilTextAsync("il[1]> nop");
            Assert.AreEqual("nop", prompt.Text);
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
            await run;
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            await IlReplApp.SettleAsync(prompt);
        }
    }

    /// <summary>
    /// Diagnostic evidence uses editor colors and action links share their target and palette through selection and scrolling.
    /// </summary>
    /// <param name="width">The terminal width.</param>
    /// <param name="height">The terminal height.</param>
    [TestMethod]
    [DataRow(100, 30)]
    [DataRow(50, 14)]
    public async Task DiagnosticHelp_ColorsEvidenceAndWrappedActionLinks(int width, int height)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        PromptState prompt = null!;
        string? opened = null;
        var adapter = new ScriptedPresentationAdapter(width, height);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(), onPrompt: state =>
        {
            prompt = state;
            state.OpenDocumentation = url => Volatile.Write(ref opened, url);
        }).WithPresentation(adapter).Build();

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = terminal.RunAsync(cancellation.Token);
        try
        {
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
            await auto.WaitUntilTextAsync("il[1]>");
            const string source = ".method int32 F() {\nldc.i4.1\nldstr \"wrong\"\ncall int32 Math::Abs(int32)\nret\n}";
            await adapter.PasteAsync(source);
            await auto.WaitUntilAsync(_ => prompt.Analysis?.Diagnostics.Any(diagnostic => diagnostic.Code == "FLOW005") == true,
                description: "the real host finds the mismatched argument");
            await auto.KeyAsync(Hex1bKey.F8, ct: ct);
            await auto.WaitUntilAsync(_ => prompt.CaretLine == 4 && prompt.Analysis is not null
                && prompt.Analyzer?.IsPending == false, description: "the call's evidence is current");
            await auto.KeyAsync(Hex1bKey.F1, ct: ct);
            await auto.WaitUntilAsync(snapshot => AppTest.Row(snapshot, 0) == "help · call"
                && AppTest.Row(snapshot, height - 1).StartsWith("Esc back", StringComparison.Ordinal),
                description: "the complete diagnostic help frame is visible");
            using (var snapshot = auto.CreateSnapshot())
            {
                AssertFrame(snapshot, width, height, "call");
                AssertColor(snapshot, "error on line 4", SpanStyle.Error);
                AssertColor(snapshot, "call int32", SpanStyle.Opcode);
                AssertColor(snapshot, "Math", SpanStyle.Type);
                AssertColor(snapshot, "Abs", SpanStyle.Member);
                AssertColor(snapshot, "Expected:", SpanStyle.Dim);
                AssertColor(snapshot, "Stack before", SpanStyle.Dim);
                var stack = Assert.ContainsSingle(snapshot.FindText("[int32, string]"));
                Assert.AreEqual(SpanPalette.Color(SpanStyle.Punctuation), snapshot.GetCell(stack.Column, stack.Line).Foreground);
                Assert.AreEqual(SpanPalette.Color(SpanStyle.Type), snapshot.GetCell(stack.Column + 1, stack.Line).Foreground);
                Assert.AreEqual(SpanPalette.Color(SpanStyle.TopType), snapshot.GetCell(stack.Column + 8, stack.Line).Foreground);
            }

            // Tab to the URL, then back to the source so both action states are observed after any necessary scrolling.
            await auto.TabAsync(ct: ct);
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("❯ https://ilrepl.dev"),
                description: "the selected documentation link has rendered");
            using (var snapshot = auto.CreateSnapshot())
            {
                AssertLinks(snapshot, InstructionReference.For("call").DocumentationUrl, SpanStyle.TopType);
                AssertColor(snapshot, "Go to", SpanStyle.Member);
                AssertFrame(snapshot, width, height, "call");
            }

            adapter.Resize(32, height);
            await auto.WaitUntilAsync(snapshot => snapshot.Width == 32 && snapshot.GetCell(31, height - 1).Character == "…",
                description: "help has rendered its footer at the narrower width");
            await auto.TabAsync(ct: ct);
            await auto.Shift().TabAsync(ct: ct);
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("❯ https://ilrepl.dev") && snapshot.ContainsText("opcodes/#call"),
                description: "the same link wraps in a narrower viewport");
            using (var snapshot = auto.CreateSnapshot())
            {
                AssertLinks(snapshot, InstructionReference.For("call").DocumentationUrl, SpanStyle.TopType);
            }

            adapter.Resize(width, height);
            await auto.WaitUntilAsync(snapshot => snapshot.Width == width && snapshot.ContainsText("https://ilrepl.dev"),
                description: "the selected target survives resizing");
            await auto.EnterAsync(ct: ct);
            await auto.WaitUntilAsync(_ => Volatile.Read(ref opened) is not null,
                description: "Enter activates the selected documentation");
            Assert.AreEqual(InstructionReference.For("call").DocumentationUrl, Volatile.Read(ref opened));
            await auto.Shift().TabAsync(ct: ct);
            await auto.WaitUntilAsync(snapshot => prompt.Help is { SelectedAction: 0 } && snapshot.ContainsText("❯ Go to"),
                description: "the selected source action is in view");
            using (var snapshot = auto.CreateSnapshot())
            {
                AssertColor(snapshot, "❯ Go to", SpanStyle.TopType);
                AssertLinks(snapshot, InstructionReference.For("call").DocumentationUrl, SpanStyle.Member);
            }

            await auto.EnterAsync(ct: ct);
            await auto.WaitUntilAsync(_ => prompt.Help is null && prompt.CaretLine == 3,
                description: "the source action returns to its producer");
            Assert.AreEqual(source, prompt.Text);
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
            await run;
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            await IlReplApp.SettleAsync(prompt);
        }
    }

    private static void AssertFrame(Hex1bTerminalSnapshot snapshot, int width, int height, string subject)
    {
        Assert.AreEqual("help · " + subject, AppTest.Row(snapshot, 0));
        AssertColor(snapshot, "help · " + subject, SpanStyle.Heading);
        Assert.AreEqual(new string('─', width), AppTest.Row(snapshot, 1));
        Assert.StartsWith("Esc back", AppTest.Row(snapshot, height - 1));
        AssertColor(snapshot, "Esc back", SpanStyle.Dim, height - 1);
        for (var row = 2; row < height - 1; row++)
        {
            Assert.StartsWith("  ", snapshot.GetLine(row), "The body keeps its two-column indent.");
        }
    }

    private static void AssertColor(Hex1bTerminalSnapshot snapshot, string text, SpanStyle style, int? line = null)
    {
        var hit = Assert.ContainsSingle(snapshot.FindText(text).Where(hit => line is null || hit.Line == line));
        var actual = snapshot.GetCell(hit.Column, hit.Line).Foreground;
        if (style == SpanStyle.Default)
        {
            Assert.IsTrue(actual is null || Equals(actual, SpanPalette.Color(style)), text + " stays in the default color.");
        }
        else
        {
            Assert.AreEqual(SpanPalette.Color(style), actual, text);
        }
    }

    private static void AssertLinks(Hex1bTerminalSnapshot snapshot, string url, SpanStyle style)
    {
        var cells = Enumerable.Range(0, snapshot.Height).SelectMany(y => Enumerable.Range(0, snapshot.Width)
            .Select(x => (X: x, Y: y, Cell: snapshot.GetCell(x, y))))
            .Where(item => item.Cell.HyperlinkData is not null).ToArray();
        Assert.IsNotEmpty(cells);
        Assert.AreSequenceEqual([url], cells.Select(item => item.Cell.HyperlinkData!.Uri).Distinct());
        var id = Assert.ContainsSingle(cells.Select(item => item.Cell.HyperlinkData!.Parameters).Distinct());
        Assert.StartsWith("id=", id);
        Assert.IsGreaterThan(3, id.Length);
        Assert.IsTrue(cells.All(item => Equals(item.Cell.Foreground, SpanPalette.Color(style))),
            "All link segments share the action color.");
        var label = string.Concat(cells.GroupBy(item => item.Y)
            .Select(row => string.Concat(row.Select(item => item.Cell.Character)).Trim()));
        Assert.Contains(url, label);
        if (snapshot.Width == 32)
        {
            Assert.IsGreaterThan(1, cells.Select(item => item.Y).Distinct().Count(), "The URL wraps at the narrow size.");
        }
    }
}
