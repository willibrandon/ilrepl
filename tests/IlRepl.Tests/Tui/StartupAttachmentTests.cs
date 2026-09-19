using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Preserves a real loaded workspace when terminal attachment overlaps startup notification delivery.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class StartupAttachmentTests
{
    /// <summary>
    /// Supplies cancellation for real host startup and terminal synchronization.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Attaching during notification delivery or after initialization restores one unchanged selected draft and history.
    /// </summary>
    /// <param name="holdNotification">Whether terminal attachment occurs while the published notification is still in flight.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Attach_RestoresPublishedWorkspaceOnce(bool holdNotification)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var saved = new SessionEditor { Lines = ["// saved café λ", "// 日本 draft"], Caret = 4, Anchor = 10, Revision = 19 };
        await files.WriteAsync(files.CompletedDocument(saved), token);
        File.Delete(files.MarkerPath);
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = new SessionController(async ct =>
        {
            await launch.Task.WaitAsync(ct);
            return await HostPaths.StartEngineAsync(ct);
        }, new SessionRequest { Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath } });

        controller.RecoveryCompleted += _ =>
        {
            published.TrySetResult();
            if (holdNotification)
            {
                release.Task.GetAwaiter().GetResult();
            }
        };

        try
        {
            launch.TrySetResult();
            await published.Task.WaitAsync(token);
            if (!holdNotification)
            {
                await controller.Initialization.WaitAsync(token);
            }

            PromptState? prompt = null;
            var transcript = new Transcript();
            await using var terminal = AppTest.Build(controller, transcript, onPrompt: value => prompt = value);
            release.TrySetResult();
            var run = IlReplApp.RunAsync(terminal, prompt, token);
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
            await controller.Initialization.WaitAsync(token);
            var expected = string.Join('\n', saved.Lines);
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("end of saved history; no code executed")
                && prompt!.Text == expected && controller.RuntimeState == SessionRuntimeState.Ready);
            Assert.AreEqual(saved.Caret, prompt!.Editor.Cursor.Position.Value);
            Assert.AreEqual(saved.Anchor, prompt.Editor.Cursor.SelectionAnchor?.Value);
            Assert.HasCount(1, transcript.Lines.Where(line =>
                line.PlainText.Contains("end of saved history; no code executed", StringComparison.Ordinal)).ToArray());
            var captured = await controller.SessionAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.Capture }, Editor = prompt.CaptureSessionEditor(),
            }, token);

            Assert.AreEqual(expected, string.Join('\n', captured.Document.Editor.Lines));
            Assert.IsFalse(captured.Dirty);
            Assert.HasCount(1, captured.Document.Cells);
            Assert.IsFalse(File.Exists(files.MarkerPath), "Attaching a terminal must not replay saved history.");
            Assert.AreEqual(0, controller.Status.Instructions);
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
            await run.WaitAsync(token);
        }
        finally
        {
            launch.TrySetResult();
            release.TrySetResult();
        }
    }
}
