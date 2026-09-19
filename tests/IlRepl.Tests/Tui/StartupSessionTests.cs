using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Preserves actual terminal editing while a real host opens its initial saved workspace.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class StartupSessionTests
{
    /// <summary>
    /// Supplies cancellation to terminal input, host startup, and notification synchronization.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Opening retains saved source and the latest live caret and selection without executing either draft or history.
    /// </summary>
    /// <param name="savedDraft">Whether the file already contains a selected unsent draft.</param>
    /// <param name="lateInput">Whether input changes while the completed workspace notification is awaiting delivery.</param>
    /// <param name="edit">Whether the user edits before startup finishes.</param>
    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Open_PreservesCurrentEditing(bool savedDraft, bool lateInput, bool edit)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var saved = savedDraft
            ? new SessionEditor { Lines = ["// saved café λ", ""], Caret = 4, Anchor = 10, Revision = 19 }
            : new SessionEditor();
        var document = files.CompletedDocument(saved);
        await files.WriteAsync(document, token);
        File.Delete(files.MarkerPath);
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = new SessionController(async ct =>
        {
            await launch.Task.WaitAsync(ct);
            return await HostPaths.StartEngineAsync(ct);
        }, new SessionRequest { Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath } });

        if (lateInput)
        {
            controller.RecoveryCompleted += _ =>
            {
                notification.TrySetResult();
                deliver.Task.GetAwaiter().GetResult();
            };
        }

        PromptState? prompt = null;
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        await using var terminal = AppTest.Build(controller, transcript,
            configure: builder => builder.WithPresentation(adapter), onPrompt: value => prompt = value);
        var run = IlReplApp.RunAsync(terminal, prompt, token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        try
        {
            await auto.WaitUntilTextAsync("starting execution host");
            const string early = "// early λ\nldc.i4.s 47\nret";
            const string late = "// latest 日本\nldc.i4.s 47\nret";
            SessionEditor? typed = null;
            if (edit)
            {
                await adapter.PasteAsync(early);
                await auto.WaitUntilAsync(_ => prompt!.Text == early);
                if (!lateInput)
                {
                    typed = await SelectEndAsync();
                }
            }

            launch.TrySetResult();
            if (lateInput)
            {
                await notification.Task.WaitAsync(token);
                await auto.Ctrl().KeyAsync(Hex1bKey.A, ct: token);
                await adapter.PasteAsync(late);
                await auto.WaitUntilAsync(_ => prompt!.Text == late);
                typed = await SelectEndAsync();
                deliver.TrySetResult();
            }

            await controller.Initialization.WaitAsync(token);
            var prefix = savedDraft && edit ? string.Join('\n', saved.Lines) + "\n" : "";
            var expected = edit ? prefix + (lateInput ? late : early) : string.Join('\n', saved.Lines);
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("end of saved history; no code executed")
                && prompt!.Text == expected && controller.RuntimeState == SessionRuntimeState.Ready);
            Assert.AreEqual(edit ? prefix.Length + typed!.Caret : saved.Caret, prompt!.Editor.Cursor.Position.Value);
            Assert.AreEqual(edit ? prefix.Length + typed!.Anchor : saved.Anchor, prompt.Editor.Cursor.SelectionAnchor?.Value);
            Assert.AreEqual(0, controller.Status.Instructions);
            Assert.IsFalse(File.Exists(files.MarkerPath), "Opening must not replay the historical cell.");
            var captured = await controller.SessionAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.Capture }, Editor = prompt.CaptureSessionEditor(),
            }, token);

            Assert.AreEqual(expected, string.Join('\n', captured.Document.Editor.Lines));
            Assert.AreEqual(edit, captured.Dirty);
            Assert.HasCount(1, captured.Document.Cells);
            if (edit)
            {
                await auto.RightAsync(ct: token);
                await auto.WaitUntilAsync(_ => prompt.Analysis?.Stack?.Render() == "[int32]"
                    && !prompt.Editor.Cursor.HasSelection);
                await auto.EnterAsync(ct: token);
                await auto.WaitUntilTextAsync("= 47 : int32");
                Assert.IsFalse(File.Exists(files.MarkerPath));
            }

            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
            if (edit)
            {
                await auto.WaitUntilTextAsync("Save changes to ");
                await auto.DownAsync(ct: token);
                await auto.EnterAsync(ct: token);
            }

            await run.WaitAsync(token);
        }
        finally
        {
            launch.TrySetResult();
            deliver.TrySetResult();
            await terminal.DisposeAsync();
        }

        async Task<SessionEditor> SelectEndAsync()
        {
            await auto.Shift().KeyAsync(Hex1bKey.LeftArrow, ct: token);
            await auto.Shift().KeyAsync(Hex1bKey.LeftArrow, ct: token);
            await auto.WaitUntilAsync(_ => prompt!.Editor.Cursor.HasSelection
                && prompt.Editor.Cursor.Position.Value == prompt.Text.Length - 2);
            return prompt!.CaptureSessionEditor();
        }
    }
}
