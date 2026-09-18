using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Preserves queued submission input until a real startup restoration has reached the terminal editor.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class StartupDeliveryTests
{
    /// <summary>
    /// Supplies cancellation to actual host startup and terminal input synchronization.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Enter observed after host readiness but before restoration is rendered retains source until the next explicit submission.
    /// </summary>
    /// <param name="savedDraft">Whether the opened document already contains unsent source.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task EnterBeforeRestoration_RetainsDraftWithoutExecuting(bool savedDraft)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var saved = new SessionEditor { Lines = savedDraft ? ["// saved draft"] : [] };
        await files.WriteAsync(files.CompletedDocument(saved), token);
        File.Delete(files.MarkerPath);
        var marker = Path.Combine(files.DirectoryPath, "new-submission.txt");
        var source = "ldstr " + LiteralParser.Escape(marker) + "\nldstr \"sent\"\n"
            + "call File::AppendAllText(string, string)\nldc.i4.s 47\nret";
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = new TaskCompletionSource<(bool Ready, bool RestorationQueued)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = new SessionController(async ct =>
        {
            await launch.Task.WaitAsync(ct);
            return await HostPaths.StartEngineAsync(ct);
        }, new SessionRequest { Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath } });
        PromptState? prompt = null;
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(120, 36);
        var entered = 0;
        await using var terminal = AppTest.Build(controller, transcript,
            configure: builder => builder.WithPresentation(adapter), onPrompt: value =>
            {
                prompt = value;
                value.FilterInput = input =>
                {
                    if (input is not Hex1bKeyEvent { Key: Hex1bKey.Enter } || Interlocked.Exchange(ref entered, 1) != 0)
                        return false;
                    launch.TrySetResult();
                    controller.Initialization.WaitAsync(token).GetAwaiter().GetResult();
                    boundary.TrySetResult((controller.RuntimeState == SessionRuntimeState.Ready,
                        value.Events.Any(item => item.Kind == SubmissionEventKind.SessionDocument && item.RuntimeRecovery)));
                    return false;
                };
            });
        var run = IlReplApp.RunAsync(terminal, prompt, token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        try
        {
            await auto.WaitUntilTextAsync("starting execution host");
            await adapter.PasteAsync(source);
            await auto.WaitUntilAsync(_ => prompt!.Text == source);
            await auto.EnterAsync(ct: token);
            var observed = await boundary.Task.WaitAsync(token);
            Assert.IsTrue(observed.Ready);
            Assert.IsTrue(observed.RestorationQueued, "The actual Enter event must precede application of the queued restoration.");
            var expected = savedDraft ? "// saved draft\n" + source : source;
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("end of saved history; no code executed")
                && prompt!.Text == expected && prompt.Submission is null);
            Assert.IsFalse(File.Exists(marker), "Startup readiness must not execute an Enter held before restoration was applied.");
            Assert.IsFalse(File.Exists(files.MarkerPath), "Opening must not replay historical instructions.");
            Assert.AreEqual(0, controller.Status.Instructions);
            var captured = await controller.SessionAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.Capture }, Editor = prompt!.CaptureSessionEditor(),
            }, token);
            Assert.HasCount(1, captured.Document.Cells);
            await auto.WaitUntilAsync(_ => prompt.Analysis?.Stack?.Render() == "[int32]");
            await auto.EnterAsync(ct: token);
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("= 47 : int32") && prompt.Submission is null);
            Assert.AreEqual("sent", await File.ReadAllTextAsync(marker, token), "Only the later explicit submission may execute.");
            captured = await controller.SessionAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.Capture }, Editor = prompt.CaptureSessionEditor(),
            }, token);
            Assert.HasCount(2, captured.Document.Cells);
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
            await auto.WaitUntilTextAsync("Save changes to ");
            await auto.DownAsync(ct: token);
            await auto.EnterAsync(ct: token);
            await run.WaitAsync(token);
        }
        finally
        {
            launch.TrySetResult();
            await terminal.DisposeAsync();
        }
    }
}
