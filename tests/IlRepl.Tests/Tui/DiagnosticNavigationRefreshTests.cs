using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Exercises diagnostic navigation when a real binding context changes at the terminal input boundary.
/// </summary>
[TestClass]
public sealed class DiagnosticNavigationRefreshTests
{
    /// <summary>
    /// Supplies cancellation for the real engine and terminal.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Catalog refresh preserves source navigation while source edits, accepted mutations, and cancellation invalidate it.
    /// </summary>
    /// <param name="change">The real context transition immediately before F8 dispatch.</param>
    /// <param name="backwards">Whether Shift reverses diagnostic navigation.</param>
    [TestMethod]
    [DataRow("assemblies", false)]
    [DataRow("assemblies", true)]
    [DataRow("document", false)]
    [DataRow("revision", false)]
    [DataRow("cancel", false)]
    public async Task F8_ContextChangesAtInputBoundaryPreserveOnlyCurrentSource(string change, bool backwards)
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        var ct = TestContext.CancellationToken;
        await using var engine = new CompletionEngine { HoldCompletion = false };
        await engine.PrimeAsync(ct);
        var adapter = new ScriptedPresentationAdapter(90, 24);
        var recorder = new FrameRecorder();
        PromptState prompt = null!;
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
            onPrompt: state => prompt = state).WithPresentation(adapter).AddPresentationFilter(recorder).Build();
        recorder.Terminal = terminal;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = terminal.RunAsync(cancellation.Token);
        try
        {
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
            await auto.WaitUntilTextAsync("il[1]>");
            const string source = ".method int32 F() {\n  ldstr \"界👩‍💻\"\n  call int32 Math::Abs(int32)\n  ret\n}";
            await adapter.PasteAsync(source);
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("error on line 3")
                && PromptDiagnostics.Current(prompt)?.Code == "FLOW005" && prompt.Analyzer?.IsPending == false);
            var original = prompt.Editor.Cursor.Position;
            var expected = original;
            var version = engine.AssemblyVersion;
            var revision = engine.Status.Revision;
            var frame = recorder.Count;
            var changed = false;
            prompt.FilterInput = input =>
            {
                if (changed || input is not Hex1bKeyEvent { Key: Hex1bKey.F8 })
                {
                    return false;
                }

                changed = true;
                engine.HoldAnalysis = true;
                switch (change)
                {
                    case "assemblies":
                        CompletionEngine.ChangeAssemblies();
                        Assert.IsGreaterThan(version, engine.AssemblyVersion);
                        Assert.AreEqual(revision, engine.Status.Revision);
                        break;
                    case "document":
                        prompt.SetText("nop\nnop", 7);
                        expected = prompt.Editor.Cursor.Position;
                        break;
                    case "revision":
                        Assert.IsTrue(engine.HandleAsync(".clear", ct).GetAwaiter().GetResult().Succeeded);
                        Assert.IsGreaterThan(revision, engine.Status.Revision);
                        break;
                    default:
                        prompt.Analyzer!.Cancel();
                        break;
                }

                return false;
            };

            if (backwards)
            {
                await auto.Shift().KeyAsync(Hex1bKey.F8, ct: ct);
            }
            else
            {
                await auto.KeyAsync(Hex1bKey.F8, ct: ct);
            }

            await auto.WaitUntilAsync(_ => changed && recorder.Count > frame
                && engine.Analyses.Any(call => !call.Release.Task.IsCompleted),
                description: "F8 is dispatched while replacement analysis remains held");
            Assert.IsNull(prompt.Analysis, "Navigation must not republish stale semantic evidence.");
            Assert.IsNull(PromptDiagnostics.Current(prompt), "Only source navigation may reuse the previous locations.");
            if (change == "assemblies")
            {
                Assert.AreEqual(3, prompt.CaretLine, "The displayed diagnostic must remain reachable during a catalog refresh.");
                Assert.AreEqual(2, prompt.CaretColumn);
            }
            else
            {
                Assert.AreEqual(expected, prompt.Editor.Cursor.Position, "Invalidated source must not be navigated.");
            }

            Assert.AreEqual(change == "document" ? "nop\nnop" : source, prompt.Text);
        }
        finally
        {
            foreach (var call in engine.Analyses)
            {
                call.Release.TrySetResult();
            }

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
            }

            await IlReplApp.SettleAsync(prompt);
        }
    }

    /// <summary>
    /// Replacing the actual runtime invalidates source navigation even when the new runtime repeats its semantic revision.
    /// </summary>
    [TestMethod]
    public async Task RuntimeReplacement_DoesNotReusePreviousDiagnosticLocations()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        var ct = TestContext.CancellationToken;
        await using var controller = new SessionController(new InProcessEngine(),
            _ => Task.FromResult<IReplEngine>(new InProcessEngine()));
        Assert.IsTrue((await controller.RestartAsync(ct)).Reply.Succeeded);
        var requester = new AnalysisRequester(controller);
        var state = new PromptState(new PromptHistory(), new CilTokenizer(controller.Vocabulary)) { Analyzer = requester };
        const string source = ".method int32 F() {\n  ldstr \"wrong\"\n  call int32 Math::Abs(int32)\n  ret\n}";
        state.SetText(source, source.Length);
        try
        {
            await WaitAsync(() =>
            {
                requester.Refresh(state);
                return state.Analysis is not null;
            });

            Assert.Contains(item => item.Code == "FLOW005", state.Analysis!.Diagnostics);
            var caret = state.Editor.Cursor.Position;
            var revision = controller.Status.Revision;
            var assemblies = controller.AssemblyVersion;
            Assert.IsTrue((await controller.RestartAsync(ct)).Reply.Succeeded);
            Assert.AreEqual(revision, controller.Status.Revision, "The epoch guard must distinguish identical semantic revisions.");
            Assert.AreNotEqual(assemblies >> 32, controller.AssemblyVersion >> 32);
            PromptDiagnostics.Move(state, false);
            Assert.AreEqual(caret, state.Editor.Cursor.Position);
            Assert.AreEqual(source, state.Text);
            Assert.IsNull(state.Analysis);
            Assert.IsNull(PromptDiagnostics.Current(state));
        }
        finally
        {
            await requester.SettleAsync(AppTest.Timeout);
        }
    }

    private async Task WaitAsync(Func<bool> ready)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        cancellation.CancelAfter(AppTest.Timeout);
        while (!ready())
        {
            await Task.Delay(1, cancellation.Token);
        }
    }
}
