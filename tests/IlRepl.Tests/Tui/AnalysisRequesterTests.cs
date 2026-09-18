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
    /// A cancelled delivery cannot publish stale stacks and the latest document waits for that delivery to settle.
    /// </summary>
    [TestMethod]
    public async Task OutOfOrderReplies_KeepTheCurrentDocument()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext)) return;
        await using var engine = new CompletionEngine { HoldAnalysis = true };
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        var requester = new AnalysisRequester(engine);
        state.SetText("nop", 3);
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 1);
        var old = engine.Analyses.First();
        await old.Prepared;
        state.SetText("ldc.i4.1\nret", 11);
        requester.Refresh(state);
        Assert.HasCount(1, engine.Analyses, "A second RPC must wait until the cancelled first one settles.");
        Assert.IsTrue(old.Cancellation.IsCancellationRequested);
        old.Release.SetResult();
        await WaitAsync(() => { requester.Refresh(state); return engine.Analyses.Count == 2; });
        Assert.IsNull(state.Analysis);
        var current = engine.Analyses.Last();
        current.Release.SetResult();
        await WaitAsync(() => { requester.Refresh(state); return state.Analysis is not null; });
        Assert.AreEqual("[int32]", state.Analysis!.Stack!.Render());
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Cancellation invalidates the request identity even when its replacement has exactly the same source.
    /// </summary>
    [TestMethod]
    public async Task SameKeyReply_DoesNotRetireReplacement()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext)) return;
        await using var engine = new CompletionEngine { HoldAnalysis = true };
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        var requester = new AnalysisRequester(engine);
        state.SetText("nop", 3);
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 1);
        var old = engine.Analyses.First();
        await old.Prepared;
        requester.Cancel();
        requester.Refresh(state);
        Assert.HasCount(1, engine.Analyses);
        old.Release.SetResult();
        await WaitAsync(() => { requester.Refresh(state); return engine.Analyses.Count == 2; });
        Assert.IsTrue(requester.IsPending);
        Assert.IsNull(state.Analysis, "The cancelled same-key response cannot satisfy the replacement request.");
        var current = engine.Analyses.Last();
        state.SetText("ldc.i4.1\nret", 11);
        requester.Refresh(state);
        Assert.IsTrue(current.Cancellation.IsCancellationRequested);
        current.Release.SetResult();
        await WaitAsync(() => { requester.Refresh(state); return engine.Analyses.Count == 3; });
        engine.Analyses.Last().Release.SetResult();
        await WaitAsync(() => { requester.Refresh(state); return state.Analysis is not null; });
        Assert.AreEqual("[int32]", state.Analysis!.Stack!.Render());
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// A genuine assembly load retires old diagnostics and shutdown waits for the sole active delivery.
    /// </summary>
    [TestMethod]
    public async Task AssemblyChangeAndShutdown_RetireOwnedWorkers()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext)) return;
        await using var engine = new CompletionEngine { HoldAnalysis = true };
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        var requester = new AnalysisRequester(engine);
        state.SetText("nop", 3);
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 1);
        await engine.Analyses.First().Prepared;
        var revision = engine.Status.Revision;
        var version = engine.AssemblyVersion;
        CompletionEngine.ChangeAssemblies();
        Assert.IsGreaterThan(version, engine.AssemblyVersion);
        Assert.AreEqual(revision, engine.Status.Revision);
        requester.Refresh(state);
        Assert.HasCount(1, engine.Analyses);
        Assert.IsNull(state.Analysis);
        var settling = requester.SettleAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(settling.IsCompleted);
        var call = engine.Analyses.Single();
        Assert.IsTrue(call.Cancellation.IsCancellationRequested);
        call.Release.SetResult();
        await settling;
        requester.Refresh(state);
        Assert.IsNull(state.Analysis);
    }

    /// <summary>
    /// Shutdown closes the real engine when delivery ignores cancellation and does not settle before its deadline.
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
        await call.Prepared;
        await requester.SettleAsync(TimeSpan.FromMilliseconds(500))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        Assert.IsTrue(call.Cancellation.IsCancellationRequested);
        Assert.IsTrue(call.Release.Task.IsCanceled);
    }

    /// <summary>
    /// Every caret position projects the real full-document result locally without sending additional analysis requests.
    /// </summary>
    [TestMethod]
    public async Task CaretMoves_ProjectWholeDocumentWithoutRpc()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext)) return;
        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        var requester = new AnalysisRequester(engine);
        state.SetText("ldc.i4.1\nret", 0);
        requester.Refresh(state);
        await WaitAsync(() => { requester.Refresh(state); return state.Analysis is not null; });
        Assert.AreEqual("[]", state.Analysis!.Stack!.Render());
        var calls = engine.Analyses.Count;
        state.Editor.SetCursorPosition(new DocumentOffset(9));
        requester.Refresh(state);
        Assert.AreEqual("[int32]", state.Analysis!.Stack!.Render());
        Assert.HasCount(calls, engine.Analyses);
        for (var index = 0; index < 20; index++) requester.Refresh(state);
        Assert.HasCount(calls, engine.Analyses);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Caret motion after typing and deleting reuses the outstanding edited-document result and projects its latest position.
    /// </summary>
    [TestMethod]
    public async Task CaretMoves_ReuseEditedDocumentWhileAnalysisIsPending()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext)) return;
        await using var engine = new CompletionEngine { HoldAnalysis = true };
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        var requester = new AnalysisRequester(engine);
        try
        {
            const string typedSource = "ldc.i4.1\nretx";
            state.SetText(typedSource, typedSource.Length);
            requester.Refresh(state);
            await WaitAsync(() => engine.Analyses.Count == 1);
            var typed = engine.Analyses.Single();
            await typed.Prepared;
            state.Editor.DeleteBackward();
            requester.Refresh(state);
            Assert.IsTrue(typed.Cancellation.IsCancellationRequested);
            typed.Release.SetResult();
            await WaitAsync(() => { requester.Refresh(state); return engine.Analyses.Count == 2; });
            var restored = engine.Analyses.Last();
            await restored.Prepared;
            for (var index = 0; index < 20; index++)
            {
                state.Editor.SetCursorPosition(new DocumentOffset(index % 2 == 0 ? 0 : 9));
                requester.Refresh(state);
                Assert.IsFalse(restored.Cancellation.IsCancellationRequested);
                Assert.HasCount(2, engine.Analyses);
            }
            restored.Release.SetResult();
            await WaitAsync(() => { requester.Refresh(state); return state.Analysis is not null; });
            Assert.AreEqual("ldc.i4.1\nret", state.Text);
            Assert.AreEqual("[int32]", state.Analysis!.Stack!.Render());
            Assert.IsEmpty(state.Analysis.Diagnostics);
            Assert.HasCount(2, engine.Analyses);
            Assert.IsFalse(requester.IsPending);
        }
        finally
        {
            foreach (var call in engine.Analyses) call.Release.TrySetResult();
            await requester.SettleAsync(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Multiple edits replace one pending document instead of creating cancelled RPCs for intermediate versions.
    /// </summary>
    [TestMethod]
    public async Task RapidEdits_CoalesceToLatestDocument()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext)) return;
        await using var engine = new CompletionEngine { HoldAnalysis = true };
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        var requester = new AnalysisRequester(engine);
        state.SetText("nop", 3);
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 1);
        for (var index = 0; index < 100; index++)
        {
            var text = "ldc.i4 " + index + "\nret";
            state.SetText(text, text.Length);
            requester.Refresh(state);
        }
        Assert.HasCount(1, engine.Analyses);
        engine.Analyses.First().Release.SetResult();
        await WaitAsync(() => { requester.Refresh(state); return engine.Analyses.Count == 2; });
        Assert.AreEqual("ldc.i4 99", engine.Analyses.Last().Request.Lines[0]);
        engine.Analyses.Last().Release.SetResult();
        await WaitAsync(() => { requester.Refresh(state); return state.Analysis is not null; });
        Assert.AreEqual("[int32]", state.Analysis!.Stack!.Render());
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
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
            var version = published.AssemblyVersion;
            do
            {
                Trace("before Tab");
                await auto.TabAsync(ct: ct);
                await auto.WaitUntilAsync(_ =>
                {
                    if (prompt.Text == "pop\n" + expected) return true;
                    if (engine.AssemblyVersion != version && prompt.Requester?.IsPending == false
                        && prompt.Completions is { } refreshed && prompt.Requester.Matches(prompt, refreshed))
                    {
                        version = refreshed.Reply.AssemblyVersion;
                        return true;
                    }

                    return false;
                },
                    description: "Tab accepts the member or a changed assembly catalog finishes refreshing it");
                // A new assembly legitimately invalidates the old query; only that change permits another Tab.
                Assert.AreSame(focused, app.FocusedNode, "a catalog refresh must also preserve editor focus");
            }
            while (prompt.Text != "pop\n" + expected);
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
