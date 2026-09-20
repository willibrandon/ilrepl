using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Checks that real operand completion exposes its full stack effect through the scrollable terminal detail pane.
/// </summary>
[TestClass]
public sealed class CompletionDetailEvidenceTests
{
    /// <summary>
    /// Supplies cancellation for the host engine and terminal.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A multi-argument operand keeps both its resolved signature and its complete scrollable stack effect.
    /// </summary>
    [TestMethod]
    public async Task OperandDetail_ScrollsThroughCompleteStackEffect()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        foreach (var line in new[]
        {
            ".method void ReviewEvidenceConsume(string text, int32 count, int64 ticks, float64 amount) {", "ret", "}",
        })
        {
            Assert.IsTrue((await engine.HandleAsync(line, ct)).Succeeded, line);
        }

        const string original = "call ReviewEvidenceConsu";
        const string effect = "Stack effect: [string, int32, int64, float64] → []";
        var recorder = new FrameRecorder();
        PromptState prompt = null!;
        await using var terminal = AppTest.Build(engine, new Transcript(), width: 48, height: 10,
            configure: builder => builder.AddPresentationFilter(recorder), onPrompt: state => prompt = state);
        recorder.Terminal = terminal;
        using var terminalCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = terminal.RunAsync(terminalCancellation.Token);
        try
        {
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
            await auto.WaitUntilTextAsync("il[2]>");
            await auto.TypeAsync(original, ct: ct);
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("detail") && prompt.Text == original
                && prompt.Requester?.IsPending == false && PromptWidget.Candidates(prompt, engine.Catalog).Count == 1
                && recorder.Frames is [.., var frame] && frame.Contains("detail"),
                description: "the real multi-argument method appears in the completion palette");
            var candidate = Assert.ContainsSingle(PromptWidget.Candidates(prompt, engine.Catalog));
            Assert.IsNotNull(candidate.FullDetail);
            Assert.Contains("ReviewEvidenceConsume(string, int32, int64, float64)", candidate.FullDetail);
            var rows = PromptWidget.DetailLines(prompt, engine.Catalog, 48);
            Assert.Contains(effect, string.Join(' ', rows));
            var target = rows.ToList().FindIndex(row => row.StartsWith("Stack effect:", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, target);
            Assert.DoesNotContain(effect, DetailText(recorder.Frames[^1]));
            var caret = prompt.Editor.Cursor.Position;
            var selection = prompt.SelectedIndex;
            var firstFrame = recorder.Count;
            for (var index = 0; index < target; index++)
            {
                await auto.KeyAsync(Hex1bKey.PageDown, ct: ct);
            }

            await auto.WaitUntilAsync(_ => prompt.DetailScroll == target && recorder.Frames is [.., var frame]
                && frame.Index >= firstFrame && DetailText(frame).Contains(effect, StringComparison.Ordinal),
                description: "PageDown exposes the complete stack effect inside the detail pane");
            Assert.AreEqual(original, prompt.Text);
            Assert.AreEqual(caret, prompt.Editor.Cursor.Position);
            Assert.AreEqual(selection, prompt.SelectedIndex);
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
                // The test stopped the terminal itself, and the cancellation that follows is the expected way for the run to end.
            }

            await IlReplApp.SettleAsync(prompt);
        }
    }

    /// <summary>
    /// Visible completion rows keep selection and detail navigation while a genuine assembly refresh reply is held.
    /// </summary>
    [TestMethod]
    public async Task OperandDetail_AssemblyRefreshPreservesNavigation()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        var ct = TestContext.CancellationToken;
        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(ct);
        foreach (var name in new[] { "ReviewNavigateAlpha", "ReviewNavigateBeta" })
        {
            foreach (var line in new[] { $".method void {name}(string text, int32 count, int64 ticks, float64 amount) {{", "ret", "}" })
            {
                Assert.IsTrue((await engine.HandleAsync(line, ct)).Succeeded, line);
            }
        }

        const string original = "call ReviewNavigate";
        const string effect = "Stack effect: [string, int32, int64, float64] → []";
        var recorder = new FrameRecorder();
        var adapter = new ScriptedPresentationAdapter(48, 12);
        PromptState prompt = null!;
        await using var terminal = AppTest.Build(engine, new Transcript(), width: 48, height: 12,
            configure: builder => builder.WithPresentation(adapter).AddPresentationFilter(recorder), onPrompt: value => prompt = value);
        recorder.Terminal = terminal;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = terminal.RunAsync(cancellation.Token);
        try
        {
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
            await auto.WaitUntilTextAsync("il[3]>");
            await adapter.PasteAsync(original);
            await auto.WaitUntilAsync(_ => engine.Calls is [.., var latest] && latest.Request.Lines[0] == original);
            engine.Calls[^1].Release.SetResult();
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("detail")
                && PromptWidget.Candidates(prompt, engine.Catalog).Count == 2);
            var before = engine.Status;
            var caret = prompt.Editor.Cursor.Position;
            var firstCall = engine.Calls.Count;
            CompletionEngine.ChangeAssemblies();
            await auto.WaitUntilAsync(snapshot => engine.Calls.Count > firstCall && snapshot.ContainsText("updating types"));
            await auto.DownAsync(ct: ct);
            await auto.WaitUntilAsync(_ => prompt.SelectedIndex == 1 && prompt.PaletteNavigated);
            Assert.AreEqual(original, prompt.Text, "Down navigates the visible palette instead of history during refresh.");
            await auto.UpAsync(ct: ct);
            await auto.WaitUntilAsync(_ => prompt.SelectedIndex == 0);
            Assert.AreEqual(original, prompt.Text, "Up navigates the visible palette instead of history during refresh.");
            var rows = PromptWidget.DetailLines(prompt, engine.Catalog, 48);
            var target = rows.ToList().FindIndex(row => row.StartsWith("Stack effect:", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, target);
            for (var index = 0; index < target; index++)
            {
                await auto.KeyAsync(Hex1bKey.PageDown, ct: ct);
            }

            await auto.WaitUntilAsync(_ => prompt.DetailScroll == target && recorder.Frames is [.., var frame]
                && DetailText(frame).Contains(effect, StringComparison.Ordinal));
            await auto.KeyAsync(Hex1bKey.PageUp, ct: ct);
            await auto.WaitUntilAsync(_ => prompt.DetailScroll == target - 1);
            Assert.AreEqual(caret, prompt.Editor.Cursor.Position);
            engine.Calls[^1].Release.SetResult();
            await auto.WaitUntilAsync(snapshot => prompt.Requester?.IsPending == false && snapshot.ContainsText("types 1/2")
                && !snapshot.ContainsText("updating"));
            Assert.AreEqual(target - 1, prompt.DetailScroll);
            Assert.AreEqual(original, prompt.Text);
            Assert.AreEqual(before, engine.Status);
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
            await run;
        }
        finally
        {
            foreach (var call in engine.Calls)
            {
                call.Release.TrySetResult();
            }

            await cancellation.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // The test stopped the terminal itself, and the cancellation that follows is the expected way for the run to end.
            }

            await IlReplApp.SettleAsync(prompt);
        }
    }

    private static string DetailText(Frame frame)
    {
        var rows = frame.Lines.SkipWhile(line => !line.StartsWith("│detail", StringComparison.Ordinal)).Skip(1)
            .TakeWhile(line => line.StartsWith('│')).Select(line => line.Trim('│', ' '));
        return string.Join(' ', rows);
    }
}
