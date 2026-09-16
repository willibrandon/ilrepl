using Hex1b;
using Hex1b.Automation;
using Hex1b.Documents;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Tests analysis publication, compact diagnostics, and recovery without accepting any stale document state.
/// </summary>
[TestClass]
public sealed class AnalysisRequesterTests
{
    /// <summary>
    /// Supplies cancellation for independently scheduled requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A superseded worker cannot overwrite a newer caret stack, even when it ignores cancellation.
    /// </summary>
    [TestMethod]
    public async Task OutOfOrderReplies_KeepTheCurrentDocument()
    {
        await using var engine = new CompletionEngine { HoldAnalysis = true };
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        var requester = new AnalysisRequester(engine);
        state.SetText("nop", 3);
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 1);
        var old = engine.Analyses.First();
        state.SetText("ldc.i4.1", 8);
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 2);
        Assert.IsTrue(old.Cancellation.IsCancellationRequested);
        var current = engine.Analyses.Last();
        current.Answer.SetResult(Reply(engine, current, "int32"));
        await WaitAsync(() => { requester.Refresh(state); return state.Analysis is not null; });
        old.Answer.SetResult(Reply(engine, old, "string"));
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
        requester.Refresh(state);
        Assert.AreEqual("[int32]", state.Analysis!.Stack!.Render());
    }

    /// <summary>
    /// A cancelled request with the same document key cannot retire or prevent cancellation of its replacement.
    /// </summary>
    [TestMethod]
    public async Task SameKeyReply_DoesNotRetireReplacement()
    {
        await using var engine = new CompletionEngine { HoldAnalysis = true };
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        var invalidations = 0;
        state.Invalidate = () => Interlocked.Increment(ref invalidations);
        var requester = new AnalysisRequester(engine);
        state.SetText("nop", 3);
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 1);
        var old = engine.Analyses.First();
        requester.Cancel();
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 2);
        var current = engine.Analyses.Last();
        old.Answer.SetResult(Reply(engine, old, "string"));
        await WaitAsync(() => Volatile.Read(ref invalidations) == 1);
        requester.Refresh(state);
        Assert.IsTrue(requester.IsPending);
        state.SetText("ret", 3);
        requester.Refresh(state);
        Assert.IsTrue(current.Cancellation.IsCancellationRequested);
        await WaitAsync(() => engine.Analyses.Count == 3);
        foreach (var call in engine.Analyses)
        {
            call.Answer.TrySetResult(Reply(engine, call, "int32"));
        }

        await requester.SettleAsync(TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
    }

    /// <summary>
    /// A changed assembly context clears old diagnostics, and shutdown settles cancelled workers before returning.
    /// </summary>
    [TestMethod]
    public async Task AssemblyChangeAndShutdown_RetireOwnedWorkers()
    {
        await using var engine = new CompletionEngine { HoldAnalysis = true };
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        var requester = new AnalysisRequester(engine);
        state.SetText("nop", 3);
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 1);
        engine.ChangeAssemblies();
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 2);
        Assert.IsNull(state.Analysis);
        var settling = requester.SettleAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(settling.IsCompleted);
        foreach (var call in engine.Analyses)
        {
            Assert.IsTrue(call.Cancellation.IsCancellationRequested);
            call.Answer.SetResult(Reply(engine, call, "string"));
        }

        await settling;
        requester.Refresh(state);
        Assert.IsNull(state.Analysis);
    }

    /// <summary>
    /// Settlement closes the engine when an analysis worker ignores cancellation.
    /// </summary>
    [TestMethod]
    public async Task Settlement_DisposesEngineWhenCancellationIsIgnored()
    {
        await using var engine = new CompletionEngine { HoldAnalysis = true };
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        var requester = new AnalysisRequester(engine);
        state.SetText("nop", 3);
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 1);
        var call = engine.Analyses.Single();

        await requester.SettleAsync(TimeSpan.FromMilliseconds(50))
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken);

        Assert.IsTrue(call.Cancellation.IsCancellationRequested);
        Assert.IsTrue(call.Answer.Task.IsCanceled);
    }

    /// <summary>
    /// Diagnostic navigation wraps in both directions without changing text, and explanations stay within three rows.
    /// </summary>
    [TestMethod]
    public async Task Diagnostics_NavigateAndFitNarrowColumns()
    {
        await using var engine = new InProcessEngine();
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        const string text = "ldstr \"界\"\nnop\nret";
        state.SetText(text, 0);
        state.Analysis = new AnalysisReply(1, 1, 1, 1, new AnalyzedStack(AnalyzedStackKind.Invalid, []), true,
            [new("TEST", AnalysisDiagnosticKind.Error, new string('界', 100), new("body", 1, 0, 3), []),
                new("TEST", AnalysisDiagnosticKind.Error, "second", new("body", 2, 0, 3), [])]);
        PromptDiagnostics.Move(state, false);
        Assert.AreEqual(2, state.CaretLine);
        PromptDiagnostics.Move(state, false);
        Assert.AreEqual(3, state.CaretLine);
        PromptDiagnostics.Move(state, false);
        Assert.AreEqual(2, state.CaretLine);
        PromptDiagnostics.Move(state, true);
        Assert.AreEqual(3, state.CaretLine);
        Assert.AreEqual(text, state.Text);
        state.Editor.SetCursorPosition(new DocumentOffset(0));
        var rows = PromptDiagnostics.Lines(state, 20);
        Assert.HasCount(3, rows);
        Assert.IsTrue(rows.All(row => DisplayWidth.GetStringWidth(row) <= 20));
        Assert.EndsWith("…", rows[^1]);
    }

    /// <summary>
    /// Diagnostic words remain whole, including when a word exactly fills a terminal row.
    /// </summary>
    /// <param name="width">The number of terminal columns available to the preview.</param>
    [TestMethod]
    [DataRow(21)]
    [DataRow(24)]
    public async Task Diagnostics_WrapAtWordBoundaries(int width)
    {
        await using var engine = new InProcessEngine();
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        state.SetText("nop", 0);
        state.Analysis = new AnalysisReply(1, 1, 1, 1, null, false,
            [new("TEST", AnalysisDiagnosticKind.Error, "both incoming paths need compatible stacks", new("body", 0, 0, 3), [])]);
        var rows = PromptDiagnostics.Lines(state, width);
        Assert.AreEqual("error on line 1: both incoming paths need compatible stacks", string.Join(' ', rows));
        Assert.IsTrue(rows.All(row => DisplayWidth.GetStringWidth(row) <= width));
    }

    /// <summary>
    /// A diagnostic arriving above an open palette preserves editor focus and the next completion keystroke.
    /// </summary>
    [TestMethod]
    public async Task DiagnosticsArriving_KeepCompletionFocused()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        const string original = "call Environment::get_CurrentManagedTh";
        const string expected = "call Environment::get_CurrentManagedThreadId()";
        // Stabilize real RPC serialization before testing focus; a first diagnostic can load new searchable assemblies.
        using (var warmup = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            warmup.CancelAfter(AppTest.Timeout);
            await engine.AnalyzeAsync(new(["pop", expected], 1, expected.Length, 1), warmup.Token);
            CompletionReply completion;
            AnalysisReply analysis;
            do
            {
                completion = await engine.CompleteAsync(new(["pop", original], 1, original.Length, null, []), warmup.Token);
                analysis = await engine.AnalyzeAsync(new(["pop", expected], 1, expected.Length, 1), warmup.Token);
            }
            while (completion.AssemblyVersion != analysis.AssemblyVersion || analysis.AssemblyVersion != engine.AssemblyVersion);
        }

        PromptState prompt = null!;
        Hex1bApp app = null!;
        AnalysisRequester analyzer = null!;
        var recorder = new FrameRecorder();
        var adapter = new ScriptedPresentationAdapter(80, 24);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
            onApp: value => app = value, onPrompt: value =>
            {
                prompt = value;
                analyzer = value.Analyzer!;
                // Attach the real analyzer after the completion palette has appeared.
                value.Analyzer = null;
            }).WithPresentation(adapter).AddPresentationFilter(recorder).Build();
        recorder.Terminal = terminal;
        using var terminalCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = terminal.RunAsync(terminalCancellation.Token);
        try
        {
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
            await auto.WaitUntilTextAsync("il[1]>");
            await adapter.PasteAsync("pop\n" + original);
            Hex1bNode? focused = null;
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("members 1/1") && prompt.Text == "pop\n" + original
                && recorder.Frames is [.., var frame] && frame.Contains("members 1/1")
                && frame.CaretRow?.TrimEnd() == "  ...> " + expected && (focused = app.FocusedNode) is EditorNode,
                description: "the complete member palette and focused editor are ready before analysis");
            Assert.IsNull(prompt.Analysis);
            var firstDiagnosticFrame = recorder.Count;
            prompt.Analyzer = analyzer;
            // With no definition provider, F12 only queues input and a render; a mid-frame Invalidate can be coalesced away.
            await auto.KeyAsync(Hex1bKey.F12, ct: ct);

            Hex1bNode? afterDiagnostic = null;
            AnalysisReply? published = null;
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("error on line 1:")
                && snapshot.ContainsText("members 1/1") && recorder.Frames is [.., var frame]
                && frame.Index >= firstDiagnosticFrame && frame.Contains("error on line 1:") && frame.Contains("members 1/1")
                && prompt.Requester?.IsPending == false && (published = prompt.Analysis) is not null
                && prompt.Completions is { } completion && prompt.Requester.Matches(prompt, completion)
                && (afterDiagnostic = app.FocusedNode) is EditorNode,
                description: "the real diagnostic renders above the palette with editor focus restored after reconciliation");
            Assert.AreSame(focused, afterDiagnostic, "arriving diagnostics must retain the focused editor");
            Assert.Contains(item => item.Code == "FLOW006" && item.Kind == AnalysisDiagnosticKind.Error && item.Location.Line == 0,
                published!.Diagnostics);
            Trace("before Tab");
            await auto.TabAsync(ct: ct);
            await auto.WaitUntilAsync(_ => prompt.Text == "pop\n" + expected,
                description: "Tab accepts the original member completion without changing the diagnostic's source");
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
            await run;
        }
        finally
        {
            await terminalCancellation.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (terminalCancellation.IsCancellationRequested)
            {
            }
            Trace("after app stopped");
            prompt.Analyzer = analyzer;
            await IlReplApp.SettleAsync(prompt);
        }

        void Trace(string stage)
        {
            var snapshot = prompt.Completions;
            TestContext.WriteLine($"{stage}: text=[{prompt.Text}], caret={prompt.Editor.Cursor.Position}, palette={prompt.Palette}");
            TestContext.WriteLine($"focus={app.FocusedNode?.GetType().Name}, capture={app.CapturedNode?.GetType().Name}");
            TestContext.WriteLine($"pending={prompt.Requester?.IsPending}, engine={engine.AssemblyVersion}, "
                + $"completion={snapshot?.Reply.AssemblyVersion}, analysis={prompt.Analysis?.AssemblyVersion}");
            TestContext.WriteLine($"matches={snapshot is not null && prompt.Requester?.Matches(prompt, snapshot) == true}, "
                + $"candidates={PromptWidget.Candidates(prompt, engine.Catalog).Count}, "
                + $"pending display={prompt.PendingDisplay is not null}");
        }
    }

    /// <summary>
    /// A late edge returns the complete block with the earlier failing instruction selected.
    /// </summary>
    [TestMethod]
    public async Task Submission_SelectsTheEarlierFailure()
    {
        await using var engine = new InProcessEngine();
        var lines = new[] { ".method void F() {", "// source line", "br NEXT", "EARLIER: pop", "NEXT: br EARLIER", "}" };
        var events = new List<SubmissionEvent>();
        var submission = new Submission(engine, lines, 0, false, _ => Task.CompletedTask, events.Add);
        await submission.Completion;
        var refusal = events.Single(item => item.Kind == SubmissionEventKind.Refused);
        Assert.AreEqual(string.Join('\n', lines), refusal.Text);
        Assert.AreEqual(3, refusal.CaretLine);
        Assert.IsTrue(refusal.Select);
        Assert.IsNull(engine.Status.OpenMethod);
    }

    private static AnalysisReply Reply(CompletionEngine engine, HeldAnalysis call, string type) =>
        new(call.Request.DocumentVersion, engine.Status.Revision, 1, engine.AssemblyVersion,
            new AnalyzedStack(AnalyzedStackKind.Known, [type]), true, []);

    private async Task WaitAsync(Func<bool> ready)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        while (!ready())
        {
            await Task.Delay(1, cancellation.Token);
        }
    }
}
