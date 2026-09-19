using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Reconstructs actual engine definitions while retaining independent editor state and historical execution.
/// </summary>
[TestClass]
public sealed class SessionRestartTests
{
    /// <summary>
    /// Supplies cancellation for real engine operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// An explicit restart captures in-process source, preserves the draft once, and makes definitions callable without replay.
    /// </summary>
    [TestMethod]
    public async Task Restart_RetainsDefinitionsAndDraftAcrossRepeatedReplacement()
    {
        var token = TestContext.CancellationToken;
        await using var controller = new SessionController(new InProcessEngine(),
            _ => Task.FromResult<IReplEngine>(new InProcessEngine()));
        foreach (var line in new[] { ".method int32 Answer() {", "ldc.i4 42", "ret", "}", "ldc.i4.1", "ret" })
        {
            var reply = await controller.HandleAsync(line, token);
            Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(item => item.PlainText)));
        }

        Assert.IsTrue((await controller.HandleAsync(".quiet on", token)).Succeeded);
        Assert.IsTrue((await controller.HandleAsync(".time on", token)).Succeeded);
        controller.Editor = new SessionEditor { Lines = ["// retained draft"], Caret = 5, Anchor = 2 };
        var first = await controller.RestartAsync(token);
        var second = await controller.RestartAsync(token);
        Assert.AreEqual(SessionRuntimeState.Ready, controller.RuntimeState);
        Assert.IsFalse(controller.Status.Mark.EchoStack);
        Assert.IsTrue(controller.Status.Mark.ShowTiming);
        Assert.AreSequenceEqual(first.Document.Editor.Lines, second.Document.Editor.Lines);
        Assert.AreEqual("// retained draft", Assert.ContainsSingle(controller.Editor.Lines));
        Assert.AreEqual(5, controller.Editor.Caret);
        Assert.AreEqual(2, controller.Editor.Anchor);
        Assert.HasCount(2, second.Document.Cells);
        Assert.IsEmpty(second.Document.Interruptions);
        Assert.DoesNotContain(line => line.Kind == LineKind.Result, second.Reply.Lines);
        Assert.IsTrue((await controller.HandleAsync("call int32 Answer()", token)).Succeeded);
        var answer = await controller.HandleAsync("ret", token);
        Assert.IsTrue(answer.Succeeded, string.Join('\n', answer.Lines.Select(item => item.PlainText)));
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), answer.Lines);
    }

    /// <summary>
    /// A cancelled initial launch leaves a usable editor and explicit restart starts one fresh engine.
    /// </summary>
    [TestMethod]
    public async Task StartupCancellation_PreservesEditorAndAllowsRestart()
    {
        var token = TestContext.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var controller = new SessionController(async ct =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.SetResult();
                await permit.Task.WaitAsync(ct);
            }

            return new InProcessEngine();
        });

        controller.Editor = new SessionEditor { Lines = [".class KeepThis {"], Caret = 3, Anchor = 3 };
        await entered.Task.WaitAsync(token);
        controller.CancelStartup();
        await controller.Initialization.WaitAsync(token);
        Assert.AreEqual(SessionRuntimeState.Unavailable, controller.RuntimeState);
        Assert.IsFalse(controller.CanHandleWithoutRuntime(".class KeepThis {"));
        Assert.IsTrue(controller.CanHandleWithoutRuntime(".help"));
        Assert.IsTrue(controller.CanHandleWithoutRuntime(".session save example.ilrepl.json"));
        var restored = await controller.RestartAsync(token);
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(".class KeepThis {", Assert.ContainsSingle(restored.Document.Editor.Lines));
        Assert.AreEqual(0, controller.Status.Instructions);
        Assert.AreEqual(SessionRuntimeState.Ready, controller.RuntimeState);
    }

    /// <summary>
    /// Replacing a healthy runtime retains the saved revision while an actual draft change still requires saving.
    /// </summary>
    /// <param name="draft">The editor text stored with the session.</param>
    /// <param name="edit">Whether the draft changes after saving.</param>
    [TestMethod]
    [DataRow("", false)]
    [DataRow("// saved draft", false)]
    [DataRow("", true)]
    [DataRow("// saved draft", true)]
    public async Task Restart_PreservesSavedRevision(string draft, bool edit)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var controller = await SessionWorkspaceFixture.StartAsync(token);
        Assert.IsTrue((await controller.HandleAsync("ldc.i4 42", token)).Succeeded);
        controller.Editor = new SessionEditor { Lines = draft.Length == 0 ? [] : [draft] };
        var saved = await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Save, Path = files.SessionPath },
            Editor = controller.Editor,
        }, token);

        Assert.IsFalse(saved.Dirty);
        var bytes = await File.ReadAllBytesAsync(files.SessionPath, token);
        controller.Editor = new SessionEditor { Lines = [edit ? "// changed draft" : draft] };
        var restarted = await controller.RestartAsync(token);
        Assert.AreEqual(edit, restarted.Dirty);
        Assert.AreEqual(files.SessionPath, restarted.Path);
        Assert.AreEqual(edit ? "// changed draft" : draft, string.Join('\n', restarted.Document.Editor.Lines));
        Assert.IsEmpty(restarted.Document.Interruptions);
        Assert.AreSequenceEqual(bytes, await File.ReadAllBytesAsync(files.SessionPath, token));
        var executed = await controller.HandleAsync("ret", token);
        Assert.IsTrue(executed.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), executed.Lines);
    }
}
