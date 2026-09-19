using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Checks every rendered frame while a diagnostic waits for replacement analysis.
/// </summary>
[TestClass]
public sealed class DiagnosticRefreshTests
{
    /// <summary>
    /// Supplies cancellation for terminal input and controlled analysis requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Typing preserves the diagnostic row until its replacement arrives, without retaining stale source navigation.
    /// </summary>
    [TestMethod]
    public async Task Typing_DoesNotMoveTheSeparatorThroughTheDiagnostic()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        var ct = TestContext.CancellationToken;
        await using var engine = new CompletionEngine { HoldAnalysis = true };
        await engine.PrimeAsync(ct);
        var recorder = new FrameRecorder();
        PromptState prompt = null!;
        var adapter = new ScriptedPresentationAdapter(80, 24);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
            onPrompt: value => prompt = value).WithPresentation(adapter).AddPresentationFilter(recorder).Build();
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.PasteAsync("ldc.i4 ");
        await auto.WaitUntilAsync(_ => engine.Analyses.LastOrDefault()?.Request.Lines[0] == prompt.Text);
        var first = engine.Analyses.Last();
        var initial = await first.Prepared;
        Assert.IsNotEmpty(initial.Diagnostics);
        var message = initial.Diagnostics[0].Message;
        first.Release.SetResult();
        await auto.WaitUntilTextAsync(message);
        var baseline = recorder.Frames.Last(frame => frame.Contains(message));
        var row = baseline.Lines.ToList().FindIndex(line => line.Contains(message, StringComparison.Ordinal));
        var start = recorder.Count;
        foreach (var character in "123")
        {
            var expected = prompt.Text + character;
            await auto.TypeAsync(character.ToString(), ct: ct);
            await auto.WaitUntilAsync(_ =>
            {
                foreach (var request in engine.Analyses.Where(request => request.Cancellation.IsCancellationRequested))
                {
                    request.Release.TrySetResult();
                }

                return prompt.Text == expected && engine.Analyses.LastOrDefault()?.Request.Lines[0] == expected
                    && prompt.Analysis is null;
            });
            var caret = prompt.Editor.Cursor.Position;
            await auto.KeyAsync(Hex1bKey.F8, ct: ct);
            await auto.WaitUntilAsync(_ => recorder.Count > start);
            Assert.AreEqual(caret, prompt.Editor.Cursor.Position, "pending presentation must not navigate stale source");
            Assert.IsEmpty(prompt.Highlighter.Diagnostics);
        }

        var frames = recorder.Since(start);
        Assert.IsNotEmpty(frames);
        foreach (var frame in frames)
        {
            Assert.Contains(message, frame.Lines[row], frame.ToString());
            Assert.AreEqual(baseline.Lines[row - 1], frame.Lines[row - 1], frame.ToString());
        }

        var current = engine.Analyses.Last();
        Assert.IsEmpty((await current.Prepared).Diagnostics);
        current.Release.SetResult();
        await auto.WaitUntilAsync(snapshot => prompt.Analysis is not null && !snapshot.ContainsText(message));
        await auto.TypeAsync("4", ct: ct);
        await auto.WaitUntilAsync(_ => prompt.Text == "ldc.i4 1234"
            && engine.Analyses.LastOrDefault()?.Request.Lines[0] == prompt.Text && prompt.Analysis is null);
        Assert.IsEmpty(PromptDiagnostics.Lines(prompt, 80), "a resolved diagnostic must not return on the next edit");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        foreach (var request in engine.Analyses)
        {
            request.Release.TrySetCanceled(ct);
        }

        await IlReplApp.SettleAsync(prompt);
    }

    /// <summary>
    /// Clearing the document or changing its binding context removes retained text and cannot revive stale diagnostics.
    /// </summary>
    /// <param name="change">The change that invalidates the displayed explanation.</param>
    [TestMethod]
    [DataRow("clear")]
    [DataRow("assemblies")]
    [DataRow("revision")]
    public async Task ContextChange_DiscardsPendingPresentation(string change)
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        var ct = TestContext.CancellationToken;
        await using var engine = new CompletionEngine { HoldAnalysis = true };
        await engine.PrimeAsync(ct);
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        var requester = new AnalysisRequester(engine);
        state.SetText("bad", 3);
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 1);
        var first = engine.Analyses.First();
        Assert.IsNotEmpty((await first.Prepared).Diagnostics);
        first.Release.SetResult();
        await WaitAsync(() =>
        {
            requester.Refresh(state);
            return state.Analysis is not null;
        });
        var display = PromptDiagnostics.Display(state);
        state.Editor.InsertText("x");
        requester.Refresh(state);
        await WaitAsync(() => engine.Analyses.Count == 2);
        Assert.AreEqual(display, PromptDiagnostics.Display(state));
        Assert.IsNull(PromptDiagnostics.Current(state));
        Assert.IsEmpty(state.Highlighter.Diagnostics);
        switch (change)
        {
            case "clear":
                state.Clear();
                break;
            case "assemblies":
                CompletionEngine.ChangeAssemblies();
                break;
            default:
                await engine.HandleAsync(".clear", ct);
                break;
        }

        requester.Refresh(state);
        Assert.HasCount(2, engine.Analyses, "A replacement waits for the cancelled active request to settle.");

        Assert.IsNull(PromptDiagnostics.Display(state));
        var stale = engine.Analyses.ElementAt(1);
        stale.Release.SetResult();
        if (change != "clear")
        {
            await WaitAsync(() =>
            {
                requester.Refresh(state);
                return engine.Analyses.Count == 3;
            });
        }

        foreach (var request in engine.Analyses)
        {
            request.Release.TrySetCanceled(ct);
        }

        await requester.SettleAsync(TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        Assert.IsNull(PromptDiagnostics.Display(state));
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
