using System.Diagnostics;
using System.Text;
using Hex1b;
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
        const string label = "il[1]> ";
        const string original = "call Environment::get_CurrentManagedTh";
        const string expected = "call Environment::get_CurrentManagedThreadId()";
        await using var engine = remote ? await HostPaths.StartEngineAsync(ct) : (IReplEngine)new InProcessEngine();
        if (remote)
        {
            // Warm the real RPC paths before testing acceptance; first serialization can load searchable assemblies.
            using var warmup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            warmup.CancelAfter(AppTest.Timeout);
            CompletionReply completion;
            AnalysisReply analysis;
            do
            {
                completion = await engine.CompleteAsync(new CompletionRequest([original], 0, original.Length, null, []), warmup.Token);
                analysis = await engine.AnalyzeAsync(new AnalysisRequest([expected], 0, expected.Length, 1), warmup.Token);
            }
            while (completion.AssemblyVersion != analysis.AssemblyVersion || analysis.AssemblyVersion != engine.AssemblyVersion);
        }

        var transcript = new Transcript();
        var recorder = new FrameRecorder();
        PromptState prompt = null!;
        await using var terminal = AppTest.Build(engine, transcript,
            configure: builder => builder.AddPresentationFilter(recorder).WithMouse(), onPrompt: value => prompt = value);
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        var firstFrame = recorder.Count;
        await auto.TypeAsync(original, ct: ct);
        // Raw snapshots can show part of a synchronized update; also require a completed frame after the action.
        await auto.WaitUntilAsync(snapshot => Ready(snapshot, original.Length, palette: true),
            description: "the bound property getter appears");
        firstFrame = recorder.Count;
        switch (acceptance)
        {
            case "enter":
                await auto.DownAsync(ct: ct);
                await auto.WaitUntilAsync(snapshot => Ready(snapshot, original.Length, palette: true)
                    && snapshot.ContainsText("Enter accepts")
                    && recorder.Frames[^1].Contains("Enter accepts"), description: "Down selects completion acceptance");
                firstFrame = recorder.Count;
                await auto.EnterAsync(ct: ct);
                break;
            case "right":
                await auto.RightAsync(ct: ct);
                break;
            case "click":
                var row = -1;
                var frame = recorder.Frames[^1];
                for (var index = 0; index < frame.Height; index++)
                {
                    if (frame.Lines[index].Contains('❯'))
                    {
                        row = index;
                        break;
                    }
                }

                Assert.IsGreaterThanOrEqualTo(0, row);
                await auto.ClickAtAsync(5, row, ct: ct);
                break;
            default:
                await auto.TabAsync(ct: ct);
                break;
        }

        await auto.WaitUntilAsync(snapshot => Ready(snapshot, expected.Length, palette: false),
            description: "the operand is accepted and the palette is closed");
        firstFrame = recorder.Count;
        await auto.Ctrl().KeyAsync(Hex1bKey.Z, ct: ct);
        await auto.WaitUntilAsync(snapshot => Ready(snapshot, original.Length, palette: true),
            description: "one undo restores the prefix, caret, and completion");
        firstFrame = recorder.Count;
        await auto.TabAsync(ct: ct);
        await auto.WaitUntilAsync(snapshot => Ready(snapshot, expected.Length, palette: false),
            description: "the restored operand is accepted again");

        bool Ready(Hex1bTerminalSnapshot snapshot, int caret, bool palette)
        {
            var frames = recorder.Frames;
            var completed = frames.Count == 0 ? null : frames[^1];
            return AppTest.PromptRow(snapshot, 0) == label + expected
                && AppTest.CaretAt(snapshot, label.Length + caret, 0)
                && (palette ? snapshot.ContainsText("members 1/1") : !snapshot.ContainsText("members"))
                && completed is not null && completed.Index >= firstFrame
                && completed.CaretRow?.TrimEnd() == label + expected
                && completed.Caret == AppTest.Caret(snapshot)
                && (palette ? completed.Contains("members 1/1") : !completed.Contains("members"));
        }

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
    /// Every part of a real long method signature remains reachable without changing the editor or selected member.
    /// </summary>
    [TestMethod]
    public async Task Operand_LongSignature_ScrollsThroughTheWholeDetail()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        var ct = TestContext.CancellationToken;
        await using var engine = new CompletionEngine { HoldCompletion = false };
        CompletionEngine.LoadLongSignature();
        await engine.PrimeAsync(ct);
        PromptState prompt = null!;
        var adapter = new ScriptedPresentationAdapter(60, 20);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
            onPrompt: value => prompt = value).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        const string source = "call LongSignatureFixture.Methods::Us";
        await adapter.PasteAsync(source);
        await auto.WaitUntilTextAsync("SignaturePart00");
        for (var index = 0; index < 80; index++)
        {
            await auto.KeyAsync(Hex1bKey.PageDown, ct: ct);
        }

        await auto.WaitUntilTextAsync("SignaturePart79");
        Assert.AreEqual(source, prompt.Text);
        Assert.AreEqual(0, prompt.SelectedIndex);
        for (var index = 0; index < 80; index++)
        {
            await auto.KeyAsync(Hex1bKey.PageUp, ct: ct);
        }

        await auto.WaitUntilTextAsync("SignaturePart00");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
    }

    /// <summary>
    /// Real replacement pages preserve the palette in every rendered frame while stale rows remain disabled.
    /// </summary>
    [TestMethod]
    public async Task Operand_ReplacementPages_KeepPaletteVisible()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        var ct = TestContext.CancellationToken;
        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(ct);
        PromptState prompt = null!;
        var recorder = new FrameRecorder();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
            onPrompt: value => prompt = value).WithPresentation(adapter).AddPresentationFilter(recorder).Build();
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        const string prefix = "call string::Su";
        await adapter.PasteAsync(prefix);
        await auto.WaitUntilAsync(_ => engine.Calls is [.., var latest] && latest.Request.Lines[0] == prefix);
        engine.Calls[^1].Release.SetResult();
        await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("members") && prompt.Completions is not null);
        var item = prompt.Completions!.Reply.Items[0];
        var firstFrame = recorder.Count;
        await auto.BackspaceAsync(ct: ct);
        await auto.BackspaceAsync(ct: ct);
        await auto.WaitUntilAsync(_ =>
        {
            foreach (var call in engine.Calls.Where(call => call.Cancellation.IsCancellationRequested))
            {
                call.Release.TrySetResult();
            }

            return engine.Calls[^1].Request.Lines[0] == "call string::";
        });
        await auto.WaitUntilTextAsync("updating members");
        Assert.IsEmpty(PromptWidget.Candidates(prompt, engine.Catalog));
        Assert.IsNull(CompletionEdit.For(prompt, item));
        engine.Calls[^1].Release.SetResult();
        await auto.WaitUntilAsync(_ => prompt.Completions is { Reply.Items.Count: CompletionReply.PageSize });
        var cursor = prompt.Completions!.Reply.Cursor;
        Assert.IsNotNull(cursor);
        await auto.KeyAsync(Hex1bKey.End, ct: ct);
        prompt.Requester!.RequestMore(prompt);
        prompt.Invalidate?.Invoke();
        await auto.WaitUntilAsync(_ => engine.Calls[^1].Request.Cursor == cursor);
        Assert.IsNotEmpty(PromptWidget.Candidates(prompt, engine.Catalog));
        engine.Calls[^1].Release.SetResult();
        await auto.WaitUntilAsync(_ => prompt.Completions!.Reply.Items.Count > CompletionReply.PageSize);
        Assert.IsTrue(recorder.Since(firstFrame).All(frame => frame.Contains("members")));
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
    }

    /// <summary>
    /// A genuine assembly refresh keeps old rows visible but disabled until their real replacements arrive.
    /// </summary>
    [TestMethod]
    public async Task Operand_AssemblyRefresh_KeepsPaletteVisible()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        var ct = TestContext.CancellationToken;
        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(ct);
        PromptState prompt = null!;
        var recorder = new FrameRecorder();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
            onPrompt: value => prompt = value).WithPresentation(adapter).AddPresentationFilter(recorder).Build();
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        const string prefix = "call Console::W";
        await adapter.PasteAsync(prefix);
        await auto.WaitUntilAsync(_ => engine.Calls is [.., var latest] && latest.Request.Lines[0] == prefix);
        engine.Calls[^1].Release.SetResult();
        await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("members") && prompt.Completions is not null);
        var item = prompt.Completions!.Reply.Items[0];
        var firstFrame = recorder.Count;
        var firstCall = engine.Calls.Count;
        CompletionEngine.ChangeAssemblies();
        await auto.WaitUntilAsync(_ => engine.Calls.Count > firstCall);
        await auto.WaitUntilTextAsync("updating members");
        Assert.IsEmpty(PromptWidget.Candidates(prompt, engine.Catalog));
        Assert.IsNull(CompletionEdit.For(prompt, item));
        engine.Calls[^1].Release.SetResult();
        await auto.WaitUntilAsync(_ => PromptWidget.Candidates(prompt, engine.Catalog).Count > 0);
        Assert.IsTrue(recorder.Since(firstFrame).All(frame => frame.Contains("members")));
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
    }

    /// <summary>
    /// Every acceptance input waits for a confirmed replacement of visible rows during a real assembly refresh.
    /// </summary>
    /// <param name="acceptance">The actual terminal input selecting the visible candidate.</param>
    [TestMethod]
    [DataRow("tab")]
    [DataRow("enter")]
    [DataRow("right")]
    [DataRow("click")]
    public async Task Operand_AcceptDuringAssemblyRefresh_AcceptsFreshBinding(string acceptance)
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        var ct = TestContext.CancellationToken;
        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(ct);
        PromptState prompt = null!;
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
            onPrompt: value => prompt = value).WithPresentation(adapter).WithMouse().Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        const string prefix = "call Environment::get_CurrentManagedTh";
        await adapter.PasteAsync(prefix);
        await auto.WaitUntilAsync(_ => engine.Calls is [.., var latest] && latest.Request.Lines[0] == prefix);
        engine.Calls[^1].Release.SetResult();
        await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("members 1/1") && prompt.Completions is not null);
        if (acceptance == "enter")
        {
            await auto.DownAsync(ct: ct);
            await auto.WaitUntilTextAsync("Enter accepts");
        }

        var selected = prompt.Completions!.Reply.Items.Single();
        var firstCall = engine.Calls.Count;
        CompletionEngine.ChangeAssemblies();
        await auto.WaitUntilAsync(_ => engine.Calls.Count > firstCall);
        await auto.WaitUntilTextAsync("updating members");
        switch (acceptance)
        {
            case "enter": await auto.EnterAsync(ct: ct); break;
            case "right": await auto.RightAsync(ct: ct); break;
            case "click":
                using (var snapshot = terminal.CreateSnapshot())
                {
                    var row = Enumerable.Range(0, snapshot.Height).First(index => snapshot.GetLine(index)
                        .Contains("get_CurrentManagedThreadId()", StringComparison.Ordinal));
                    await auto.ClickAtAsync(5, row, ct: ct);
                }

                break;
            default: await auto.TabAsync(ct: ct); break;
        }

        await auto.WaitUntilAsync(_ => prompt.Requester!.HasPendingAcceptance);
        Assert.AreEqual(prefix, prompt.Text);
        Assert.IsNull(CompletionEdit.For(prompt, selected));
        engine.Calls[^1].Release.SetResult();
        await auto.WaitUntilAsync(snapshot => prompt.Text == "call Environment::get_CurrentManagedThreadId()"
            && !snapshot.ContainsText("members"));
        await auto.Ctrl().KeyAsync(Hex1bKey.Z, ct: ct);
        await auto.WaitUntilAsync(_ => prompt.Text == prefix);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        foreach (var call in engine.Calls)
        {
            call.Release.TrySetResult();
        }

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
