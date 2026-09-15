using System.Diagnostics;
using System.Text;
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
    /// Consecutive terminal Backspace bytes remove an invalid argument while preserving its selected generic owner.
    /// </summary>
    [TestMethod]
    public async Task Operand_InvalidGenericArgument_CanBeDeleted()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        await engine.HandleAsync(".load " + SampleHost.Samples.GreeterDll, ct);
        var transcript = new Transcript();
        PromptState prompt = null!;
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1b.Hex1bTerminal.CreateBuilder(), engine, transcript,
            onPrompt: value => prompt = value).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("call Greeter.Generic::Constrained", ct: ct);
        await auto.WaitUntilTextAsync("members 1/1");
        await auto.TabAsync(ct: ct);
        await auto.WaitUntilAsync(_ => prompt.Text == "call Generic::Constrained<", description: "the selected owner is inserted");
        await auto.TypeAsync("string>", ct: ct);
        await auto.WaitUntilAsync(_ => prompt.Text == "call Generic::Constrained<string>"
            && PromptWidget.Candidates(prompt, engine.Catalog).Count == 0, description: "the invalid argument has no signature");
        await adapter.SendAsync(Encoding.UTF8.GetBytes(new string('\x7f', "string>".Length)));

        await auto.WaitUntilAsync(_ => prompt.Text == "call Generic::Constrained<", description: "all seven characters were deleted");
        await auto.TypeAsync("int32>", ct: ct);
        await auto.WaitUntilTextAsync("signatures 1/1");
        await auto.TabAsync(ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("stack [int32]");
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 0 : int32");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Every acceptance input inserts one undoable edit whose restored completion binds and executes through either transport.
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
        // Assembly loads in other tests invalidate in-process rows between the rendered palette and the acceptance key.
        // Keep this interaction's catalog isolated; AssemblyCompletionTests covers invalidation separately.
        if (!remote && await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        var ct = TestContext.CancellationToken;
        await using var engine = remote ? await HostPaths.StartEngineAsync(ct) : (IReplEngine)new InProcessEngine();
        var transcript = new Transcript();
        PromptState prompt = null!;
        await using var terminal = AppTest.Build(engine, transcript, configure: builder => builder.WithMouse(),
            onPrompt: value => prompt = value);
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        const string label = "il[1]> ";
        const string original = "call Environment::get_CurrentManagedTh";
        const string expected = "call Environment::get_CurrentManagedThreadId()";
        await auto.TypeAsync(original, ct: ct);
        // Automation runs outside the UI thread. Read its immutable snapshots, not the editor or requester being updated.
        await auto.WaitUntilAsync(snapshot => AppTest.PromptRow(snapshot, 0) == label + expected
            && AppTest.CaretAt(snapshot, label.Length + original.Length, 0)
            && snapshot.ContainsText("members 1/1"), description: "the bound property getter appears");
        switch (acceptance)
        {
            case "enter":
                await auto.DownAsync(ct: ct);
                await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("members 1/1")
                    && snapshot.ContainsText("Enter accepts"), description: "Down selects completion acceptance");
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

        await auto.WaitUntilAsync(snapshot => AppTest.PromptRow(snapshot, 0) == label + expected
            && AppTest.CaretAt(snapshot, label.Length + expected.Length, 0)
            && !snapshot.ContainsText("members"), description: "the operand is accepted and the palette is closed");
        await auto.Ctrl().KeyAsync(Hex1bKey.Z, ct: ct);
        await auto.WaitUntilAsync(snapshot => AppTest.PromptRow(snapshot, 0) == label + expected
            && AppTest.CaretAt(snapshot, label.Length + original.Length, 0)
            && snapshot.ContainsText("members 1/1"), description: "one undo restores the prefix, caret, and completion");
        await auto.TabAsync(ct: ct);
        await auto.WaitUntilAsync(snapshot => AppTest.PromptRow(snapshot, 0) == label + expected
            && AppTest.CaretAt(snapshot, label.Length + expected.Length, 0)
            && !snapshot.ContainsText("members"), description: "the restored operand is accepted again");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("stack [int32]");
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync(": int32");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
        // Inspect the mutable transcript only after the app has stopped; no acceptance path may submit an extra line.
        var echoes = AppTest.Echoes(transcript);
        Assert.HasCount(2, echoes);
        Assert.EndsWith(expected, echoes[0]);
        Assert.EndsWith("ret", echoes[1]);
        Assert.DoesNotContain(LineKind.Error, transcript.Lines.Select(line => line.Kind));
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
    /// Empty replacement pages preserve the palette in every rendered frame while disabling the retained rows.
    /// </summary>
    [TestMethod]
    public async Task Operand_EmptyReplacementPages_KeepPaletteVisible()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new CompletionEngine();
        PromptState prompt = null!;
        var recorder = new FrameRecorder();
        await using var terminal = AppTest.Build(engine, new Transcript(),
            configure: builder => builder.AddPresentationFilter(recorder), onPrompt: value => prompt = value);
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        const string prefix = "call Console::W";
        await auto.TypeAsync(prefix, ct: ct);
        await auto.WaitUntilAsync(_ => engine.Calls.LastOrDefault()?.Request.Lines[0] == prefix,
            description: "the first operand request arrives");
        var item = new CompletionItem("WriteLine", "[] → void", "Console", false)
        {
            Kind = CompletionKind.Members, Insert = "Console::WriteLine()",
        };
        var reply = new CompletionReply(CompletionKind.Members, 5, 10, [item], null, 1, false,
            engine.Status.Revision, "initial", 1, []);
        engine.Calls[^1].Answer.SetResult(reply);
        await auto.WaitUntilTextAsync("members");
        var firstFrame = recorder.Count;
        await auto.TypeAsync("r", ct: ct);
        await auto.WaitUntilAsync(_ => engine.Calls[^1].Request.Lines[0] == prefix + "r",
            description: "the replacement request arrives");
        reply = reply with { ReplaceLength = 11, QueryId = "replacement" };
        for (var page = 2; page <= 3; page++)
        {
            var cursor = "page-" + page;
            engine.Calls[^1].Answer.SetResult(reply with { Items = [], Cursor = cursor, TotalIsProvisional = true });
            await auto.WaitUntilAsync(_ => engine.Calls[^1].Request.Cursor == cursor,
                description: "the empty page is followed");
            await auto.WaitUntilTextAsync("updating members");
            Assert.IsEmpty(PromptWidget.Candidates(prompt, engine.Catalog));
            Assert.IsNull(CompletionEdit.For(prompt, item));
        }

        engine.Calls[^1].Answer.SetResult(reply);
        await auto.WaitUntilAsync(_ => PromptWidget.Candidates(prompt, engine.Catalog).Count == 1,
            description: "the replacement row becomes current");
        Assert.IsTrue(recorder.Since(firstFrame).All(frame => frame.Contains("members")));
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
    }

    /// <summary>
    /// An assembly refresh keeps old rows visible but disabled until their replacements arrive.
    /// </summary>
    [TestMethod]
    public async Task Operand_AssemblyRefresh_KeepsPaletteVisible()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new CompletionEngine();
        PromptState prompt = null!;
        var recorder = new FrameRecorder();
        await using var terminal = AppTest.Build(engine, new Transcript(),
            configure: builder => builder.AddPresentationFilter(recorder), onPrompt: value => prompt = value);
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        const string prefix = "call Console::W";
        await auto.TypeAsync(prefix, ct: ct);
        await auto.WaitUntilAsync(_ => engine.Calls.LastOrDefault()?.Request.Lines[0] == prefix,
            description: "the first operand request arrives");
        var item = new CompletionItem("WriteLine", "[] → void", "Console", false)
        {
            Kind = CompletionKind.Members, Insert = "Console::WriteLine()",
        };
        var reply = new CompletionReply(CompletionKind.Members, 5, 10, [item], null, 1, false,
            engine.Status.Revision, "initial", 1, []) { AssemblyVersion = engine.AssemblyVersion };
        engine.Calls[^1].Answer.SetResult(reply);
        await auto.WaitUntilTextAsync("members");
        var firstFrame = recorder.Count;
        var firstCall = engine.Calls.Count;
        engine.ChangeAssemblies();
        await auto.WaitUntilAsync(_ => engine.Calls.Count > firstCall,
            description: "the changed assembly catalog starts a replacement request");
        await auto.WaitUntilTextAsync("updating members");
        Assert.IsEmpty(PromptWidget.Candidates(prompt, engine.Catalog));
        Assert.IsNull(CompletionEdit.For(prompt, item));
        reply = reply with { QueryId = "replacement", AssemblyVersion = engine.AssemblyVersion };
        engine.Calls[^1].Answer.SetResult(reply);
        await auto.WaitUntilAsync(_ => PromptWidget.Candidates(prompt, engine.Catalog).Count == 1,
            description: "the replacement row becomes current");
        Assert.IsTrue(recorder.Since(firstFrame).All(frame => frame.Contains("members")));
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
    }

    /// <summary>
    /// A typed operand reaches the real host and returns as visible, current rows within the input budget.
    /// </summary>
    [TestMethod]
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
            var missing = recorder.Since(firstFrame).Where(frame => !frame.Lines.Any(line => line.Contains("members",
                StringComparison.Ordinal))).ToArray();
            Assert.IsEmpty(missing, $"An operand edit must not collapse and reopen the palette between replies (edit {index}, "
                + $"assembly {prompt.Completions?.Reply.AssemblyVersion}/{engine.AssemblyVersion}).\n"
                + string.Join("\n---\n", missing.Select(frame => string.Join("\n", frame.Lines))));
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
