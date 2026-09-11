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
        await requester.SettleAsync();
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
        foreach (var call in engine.Analyses)
        {
            call.Answer.TrySetResult(Reply(engine, call, "int32"));
        }

        await requester.SettleAsync();
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
        var settling = requester.SettleAsync();
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
    /// Ordinary diagnostic words remain whole when an explanation wraps to another terminal row.
    /// </summary>
    [TestMethod]
    public async Task Diagnostics_WrapAtWordBoundaries()
    {
        await using var engine = new InProcessEngine();
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        state.SetText("nop", 0);
        state.Analysis = new AnalysisReply(1, 1, 1, 1, null, false,
            [new("TEST", AnalysisDiagnosticKind.Error, "both incoming paths need compatible stacks", new("body", 0, 0, 3), [])]);
        var rows = PromptDiagnostics.Lines(state, 24);
        Assert.AreEqual("error on line 1: both incoming paths need compatible stacks", string.Join(' ', rows));
        Assert.IsTrue(rows.All(row => DisplayWidth.GetStringWidth(row) <= 24));
    }

    /// <summary>
    /// A diagnostic arriving above an open palette preserves editor focus and the next completion keystroke.
    /// </summary>
    [TestMethod]
    public async Task DiagnosticsArriving_KeepCompletionFocused()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new CompletionEngine { HoldAnalysis = true };
        engine.Immediate = new CompletionReply(CompletionKind.Members, 5, 11,
            [new CompletionItem("WriteLine", "[] → void", "Console", false)
            {
                Kind = CompletionKind.Members, Insert = "Console::WriteLine()",
            }], null, 1, false, engine.Status.Revision, "focus", 1, []);
        PromptState prompt = null!;
        Hex1bApp app = null!;
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
            onApp: value => app = value, onPrompt: value => prompt = value).WithHeadless().WithDimensions(80, 24).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("call Console::Wr", ct: ct);
        await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("members 1/1")
            && engine.Analyses.LastOrDefault()?.Request.Lines[0] == prompt.Text);
        var focused = app.FocusedNode;
        Assert.IsNotNull(focused);
        var pending = engine.Analyses.Last();
        pending.Answer.SetResult(new AnalysisReply(pending.Request.DocumentVersion, engine.Status.Revision, 1, engine.AssemblyVersion,
            new AnalyzedStack(AnalyzedStackKind.Known, []), true,
            [new("TEST", AnalysisDiagnosticKind.Incomplete, "finish the member name", new("document", 0, 5, 11), [])]));
        await auto.WaitUntilTextAsync("finish the member name");
        Assert.AreSame(focused, app.FocusedNode, "arriving diagnostics must retain the focused editor");
        await auto.TabAsync(ct: ct);
        await auto.WaitUntilAsync(_ => prompt.Text == "call Console::WriteLine()");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        foreach (var request in engine.Analyses)
        {
            request.Answer.TrySetCanceled(ct);
        }

        await IlReplApp.SettleAsync(prompt);
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
