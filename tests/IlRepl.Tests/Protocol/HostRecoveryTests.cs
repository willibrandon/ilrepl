using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Recovers acknowledged source after actual execution-process failures without replaying cells.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class HostRecoveryTests
{
    /// <summary>
    /// Supplies cancellation for real host startup and recovery.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// An unexpected successful process exit is a host failure, and recovered definitions remain callable.
    /// </summary>
    [TestMethod]
    public async Task UnexpectedExit_RetainsSourceAndCallableDefinitions()
    {
        var token = TestContext.CancellationToken;
        var initial = await HostPaths.StartEngineAsync(token);
        var originalPid = initial.ProcessId;
        await using var controller = new SessionController(initial, async ct => await HostPaths.StartEngineAsync(ct));
        var recovered = new TaskCompletionSource<SessionReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RecoveryCompleted += reply => recovered.TrySetResult(reply);
        foreach (var line in new[] { ".method int32 Answer() {", "ldc.i4 42", "ret", "}", "ldc.i4.0",
            "call void System.Environment::Exit(int32)" })
        {
            var reply = await controller.HandleAsync(line, token);
            Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(item => item.PlainText)));
        }

        _ = await controller.HandleAsync("ret", token);
        var workspace = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.AreEqual(SessionRuntimeState.Ready, controller.RuntimeState);
        Assert.AreEqual(originalPid, controller.LastHostExit!.ProcessId);
        Assert.AreEqual(0, controller.LastHostExit.ExitCode);
        Assert.AreEqual("interrupted", workspace.Document.Cells.Single(cell => cell.Kind == "cell").State);
        Assert.AreEqual(0, Assert.ContainsSingle(workspace.Document.Interruptions).ExitCode);
        var roundTrip = SessionCodec.Read(SessionCodec.Write(workspace.Document));
        Assert.AreEqual("interrupted", roundTrip.Cells.Single(cell => cell.Kind == "cell").State);
        Assert.IsTrue((await controller.HandleAsync("call int32 Answer()", token)).Succeeded);
        var result = await controller.HandleAsync("ret", token);
        Assert.IsTrue(result.Succeeded, string.Join('\n', result.Lines.Select(item => item.PlainText)));
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
    }

    /// <summary>
    /// A fatal replay retains the entire experiment, records the interrupted cell, and leaves saving available after launch failure.
    /// </summary>
    /// <param name="recoveryFails">Whether the first replacement launch fails after the actual replay host exits.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplayExit_PreservesExperimentAndExitDetails(bool recoveryFails)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        string[] crash = ["ldc.i4.s 17", "call void System.Environment::Exit(int32)", "ret"];
        string[] later = ["ldc.i4 42", "ret"];
        var document = new SessionDocument
        {
            Entries =
            [
                new SessionEntry { Source = crash[..^1] },
                new SessionEntry { Kind = SessionEntryKind.Run, Source = ["ret"] },
                new SessionEntry { Number = 2, Source = later[..^1] },
                new SessionEntry { Number = 2, Kind = SessionEntryKind.Run, Source = ["ret"] },
            ],
            Cells = [new SessionCell { Source = crash }, new SessionCell { Number = 2, Source = later }],
            Editor = new SessionEditor { Lines = ["// keep the draft"], Caret = 4, Anchor = 2 },
        };
        await files.WriteAsync(document, token);
        var original = await File.ReadAllBytesAsync(files.SessionPath, token);
        var running = false;
        var launches = 0;
        await using var controller = new SessionController(await HostPaths.StartEngineAsync(token), async ct =>
        {
            if (running && Interlocked.Increment(ref launches) == 2 && recoveryFails)
            {
                throw new IOException("replacement launch failed");
            }

            return await HostPaths.StartEngineAsync(ct);
        });
        await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
        }, token);
        var epoch = controller.AssemblyVersion >> 32;
        running = true;
        var result = await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Run }, Editor = controller.Editor,
        }, token);
        Assert.IsFalse(result.Reply.Succeeded);
        Assert.AreEqual(epoch + 1, controller.AssemblyVersion >> 32);
        Assert.AreEqual(recoveryFails ? SessionRuntimeState.Unavailable : SessionRuntimeState.Ready, controller.RuntimeState);
        Assert.AreEqual(17, controller.LastHostExit!.ExitCode);
        Assert.AreEqual(17, Assert.ContainsSingle(result.Document.Interruptions).ExitCode);
        Assert.AreSequenceEqual(["interrupted", "unrun"], result.Document.Cells.Select(cell => cell.State));
        Assert.AreSequenceEqual(document.Cells.Select(cell => cell.Identity), result.Document.Cells.Select(cell => cell.Identity));
        Assert.AreSequenceEqual(document.Entries.Select(entry => entry.Identity), result.Document.Entries.Select(entry => entry.Identity));
        Assert.AreSequenceEqual(document.Editor.Lines, result.Document.Editor.Lines);
        Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(files.SessionPath, token));
        var saved = await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Save, Path = files.SessionPath }, Editor = controller.Editor,
        }, token);
        Assert.IsFalse(saved.Dirty);
        var reopened = SessionCodec.Read(await File.ReadAllBytesAsync(files.SessionPath, token));
        Assert.AreEqual("interrupted", reopened.Cells[0].State);
        Assert.AreEqual(17, Assert.ContainsSingle(reopened.Interruptions).ExitCode);
        if (recoveryFails)
        {
            await controller.RestartAsync(token);
        }

        var replayed = await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Run, Numbers = [2] }, Editor = controller.Editor,
        }, token);
        Assert.IsTrue(replayed.Reply.Succeeded, string.Join('\n', replayed.Reply.Lines.Select(line => line.PlainText)));
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), replayed.Reply.Lines);
    }
}
