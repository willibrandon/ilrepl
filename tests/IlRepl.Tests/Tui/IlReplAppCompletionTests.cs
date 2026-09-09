using System.Diagnostics;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Drives operand discovery and acceptance through the real prompt and both engine transports.
/// </summary>
[TestClass]
public sealed class IlReplAppCompletionTests
{
    /// <summary>
    /// Supplies cancellation for terminal and host work.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Tab, navigated Enter, Right with a ghost and clicking insert the same complete operand and undo together.
    /// </summary>
    /// <param name="acceptance">The acceptance input.</param>
    /// <param name="remote">Whether the real host process serves completion.</param>
    [TestMethod]
    [DataRow("tab", false)]
    [DataRow("enter", false)]
    [DataRow("right", false)]
    [DataRow("click", false)]
    [DataRow("tab", true)]
    [DataRow("enter", true)]
    [DataRow("right", true)]
    [DataRow("click", true)]
    public async Task Operand_AcceptancePaths_ReplaceOnceAndUndo(string acceptance, bool remote)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = remote ? await HostPaths.StartEngineAsync(ct) : (IReplEngine)new InProcessEngine();
        var transcript = new Transcript();
        PromptState prompt = null!;
        await using var terminal = AppTest.Build(engine, transcript, configure: builder => builder.WithMouse(),
            onPrompt: value => prompt = value);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        const string original = "call Environment::get_CurrentManagedTh";
        await auto.TypeAsync(original, ct: ct);
        await auto.WaitUntilAsync(_ => PromptWidget.Candidates(prompt, engine.Catalog).Count == 1,
            description: "the bound property getter appears");
        await auto.WaitUntilTextAsync("members");
        var expected = "call " + PromptWidget.Candidates(prompt, engine.Catalog)[0].InsertText;
        switch (acceptance)
        {
            case "enter":
                await auto.DownAsync(ct: ct);
                await auto.EnterAsync(ct: ct);
                break;
            case "right":
                await auto.RightAsync(ct: ct);
                break;
            case "click":
                var row = -1;
                using (var snapshot = terminal.CreateSnapshot())
                {
                    for (var index = 0; index < snapshot.Height; index++)
                    {
                        if (snapshot.GetLine(index).Contains('❯'))
                        {
                            row = index;
                            break;
                        }
                    }
                }

                Assert.IsGreaterThanOrEqualTo(0, row);
                await auto.ClickAtAsync(5, row, ct: ct);
                break;
            default:
                await auto.TabAsync(ct: ct);
                break;
        }

        await auto.WaitUntilAsync(_ => prompt.Text == expected && prompt.PaletteDismissed, description: "the operand is accepted");
        Assert.IsEmpty(AppTest.Echoes(transcript));
        await auto.Ctrl().KeyAsync(Hex1bKey.Z, ct: ct);
        await auto.WaitUntilAsync(_ => prompt.Text == original, description: "one undo restores the typed prefix");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
    }
    /// <summary>
    /// Every part of a long signature remains reachable without changing the editor or selected member.
    /// </summary>
    [TestMethod]
    public async Task Operand_LongSignature_ScrollsThroughTheWholeDetail()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new CompletionEngine();
        var signature = string.Join("\n", Enumerable.Range(0, 80).Select(index => "signature part " + index));
        engine.Immediate = new CompletionReply(CompletionKind.Members, 5, 11,
            [new CompletionItem("WriteLine", "[] → void", "Console", false)
            {
                Kind = CompletionKind.Members, Insert = "Console::WriteLine()", FullDetail = signature,
            }], null, 1, false, engine.Status.Revision, "long-signature", 1, []);
        PromptState prompt = null!;
        await using var terminal = AppTest.Build(engine, new Transcript(), width: 60, height: 20,
            onPrompt: value => prompt = value);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("call Console::Wr", ct: ct);
        await auto.WaitUntilTextAsync("signature part 0");
        for (var index = 0; index < 80; index++)
        {
            await auto.KeyAsync(Hex1bKey.PageDown, ct: ct);
        }

        await auto.WaitUntilTextAsync("signature part 79");
        Assert.AreEqual("call Console::Wr", prompt.Text);
        Assert.AreEqual(0, prompt.SelectedIndex);
        for (var index = 0; index < 80; index++)
        {
            await auto.KeyAsync(Hex1bKey.PageUp, ct: ct);
        }

        await auto.WaitUntilTextAsync("signature part 0");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
    }

    /// <summary>
    /// A typed operand reaches the real host and returns as visible, current rows within the input budget.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task Operand_HostKeystroke_RoundTripsWithinBudget()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        PromptState prompt = null!;
        var recorder = new FrameRecorder();
        await using var terminal = AppTest.Build(engine, new Transcript(),
            configure: builder => builder.AddPresentationFilter(recorder), onPrompt: value => prompt = value);
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal,
            new Hex1bTerminalInputSequenceOptions { PollInterval = TimeSpan.FromMilliseconds(5) }, AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        const string prefix = "call Console::W";
        await auto.TypeAsync(prefix, ct: ct);
        await auto.WaitUntilTextAsync("members");
        var times = new List<double>();
        for (var index = 0; index < 12; index++)
        {
            var extending = index % 2 == 0;
            var expected = prefix + (extending ? "r" : "");
            var firstFrame = recorder.Count;
            var watch = Stopwatch.StartNew();
            if (extending)
            {
                await auto.TypeAsync("r", ct: ct);
            }
            else
            {
                await auto.BackspaceAsync(ct: ct);
            }

            await auto.WaitUntilAsync(snapshot => prompt.Completions?.Key.Document.Lines[0] == expected
                && PromptWidget.Candidates(prompt, engine.Catalog).Count > 0
                && snapshot.GetText().Contains("members", StringComparison.Ordinal),
                description: "the edited operand's current rows are painted");
            times.Add(watch.Elapsed.TotalMilliseconds);
            Assert.IsTrue(recorder.Since(firstFrame).All(frame => frame.Lines.Any(line => line.Contains("members",
                StringComparison.Ordinal))), "An operand edit must not collapse and reopen the palette between replies.");
        }

        times.Sort();
        var median = (times[5] + times[6]) / 2;
        TestContext.WriteLine($"Host keystroke median: {median:F1} ms; maximum: {times[^1]:F1} ms.");
        Assert.IsLessThan(1000, median, "The CI input budget is one second; the reference desktop target is 150 ms.");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
    }
}
