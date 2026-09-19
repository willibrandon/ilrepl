using System.Collections.Concurrent;
using System.Diagnostics;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Exercises retained pasted source through real hosts, crashes, and acknowledged execution boundaries.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class HostedSubmissionCheckpointTests
{
    /// <summary>
    /// Supplies cancellation for actual host processes and protocol acknowledgement barriers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Increasing a pasted method's body does not increase checkpoint round trips or lose executable source.
    /// </summary>
    /// <param name="instructions">The number of ordinary instructions in the body.</param>
    [TestMethod]
    [DataRow(8)]
    [DataRow(196)]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task Paste_CheckpointsBoundariesAndCommitsCompleteMethod(int instructions)
    {
        var token = TestContext.CancellationToken;
        var host = await HostPaths.StartEngineAsync(token);
        await using var controller = new SessionController(host, async ct => await HostPaths.StartEngineAsync(ct));
        var checkpoints = new ConcurrentQueue<SessionReply>();
        host.CheckpointReceived += checkpoints.Enqueue;
        string[] source = [".method int32 Answer() {", "ldc.i4.s 42", .. Enumerable.Repeat("nop", instructions), "ret", "}"];
        for (var index = 0; index < source.Length; index++)
        {
            controller.PendingInput = source[(index + 1)..];
            var reply = await controller.HandleSourceAsync(source[index], new AnalysisLocation("paste", index, 0,
                source[index].Length), token);
            Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(line => line.PlainText)));
        }

        Assert.IsLessThan(8, checkpoints.Count, "Ordinary instructions must reuse the source retained by the frontend.");
        Assert.AreSequenceEqual(source, checkpoints.Last().Document.Entries.SelectMany(entry => entry.Source));
        Assert.AreEqual("definition", Assert.ContainsSingle(checkpoints.Last().Document.Cells).Kind);
        Assert.IsEmpty(checkpoints.Last().Document.Editor.Lines);
        Assert.IsTrue((await controller.HandleAsync("call int32 Answer()", token)).Succeeded);
        var result = await controller.HandleAsync("ret", token);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
    }

    /// <summary>
    /// A run of retained instructions is answered line by line from one host operation.
    /// </summary>
    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task Run_AnswersEveryInstructionFromOneHostOperation()
    {
        var token = TestContext.CancellationToken;
        var host = await HostPaths.StartEngineAsync(token);
        await using var controller = new SessionController(host, async ct => await HostPaths.StartEngineAsync(ct));
        var checkpoints = new ConcurrentQueue<SessionReply>();
        host.CheckpointReceived += checkpoints.Enqueue;
        var operations = new ConcurrentDictionary<string, bool>();
        string[] source = [".method int32 Answer() {", "ldc.i4.s 42", .. Enumerable.Repeat("nop", 40), "ret", "}"];
        controller.PendingInput = source[1..];
        Assert.IsTrue((await controller.HandleSourceAsync(source[0], new AnalysisLocation("paste", 0, 0, source[0].Length), token))
            .Succeeded);
        host.ProgressChanged += progress => operations.TryAdd(progress.Identity, true);
        controller.PendingInput = source[2..];
        var replies = await controller.HandleSourceRunAsync(source[1..], Locations(source, 1), token);

        // The instruction before ret closes the run, because ret is a boundary that checkpoints on its own.
        Assert.HasCount(41, replies);
        Assert.IsTrue(replies.All(reply => reply.Succeeded));
        Assert.HasCount(1, operations);
        Assert.HasCount(1, checkpoints);
        Assert.AreEqual(41, replies[^1].Status.Instructions);
        for (var index = 42; index < source.Length; index++)
        {
            controller.PendingInput = source[(index + 1)..];
            Assert.IsTrue((await controller.HandleSourceAsync(source[index],
                new AnalysisLocation("paste", index, 0, source[index].Length), token)).Succeeded);
        }

        Assert.AreSequenceEqual(source, checkpoints.Last().Document.Entries.SelectMany(entry => entry.Source));
        Assert.IsTrue((await controller.HandleAsync("call int32 Answer()", token)).Succeeded);
        var result = await controller.HandleAsync("ret", token);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
    }

    /// <summary>
    /// A run ends at the line the host refuses, leaving the lines after it unsent.
    /// </summary>
    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task Run_EndsAtTheRefusedLine()
    {
        var token = TestContext.CancellationToken;
        var host = await HostPaths.StartEngineAsync(token);
        await using var controller = new SessionController(host, async ct => await HostPaths.StartEngineAsync(ct));
        var checkpoints = new ConcurrentQueue<SessionReply>();
        host.CheckpointReceived += checkpoints.Enqueue;
        string[] source = [".method void Body() {", "nop", "ldc.i4 invalid", "nop", "nop", "ret", "}"];
        controller.PendingInput = source[1..];
        Assert.IsTrue((await controller.HandleSourceAsync(source[0], new AnalysisLocation("paste", 0, 0, source[0].Length), token))
            .Succeeded);
        controller.PendingInput = source[2..];
        var replies = await controller.HandleSourceRunAsync(source[1..], Locations(source, 1), token);
        Assert.HasCount(2, replies);
        Assert.IsTrue(replies[0].Succeeded);
        Assert.IsFalse(replies[1].Succeeded);
        Assert.AreEqual(1, replies[1].Status.Instructions);
        Assert.AreEqual(SessionEntryKind.Rejected, checkpoints.Last().Document.Entries.Last().Kind);
    }

    /// <summary>
    /// The host takes no line of a run outside an open method, so the frontend handles it as a single line.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Run_OutsideAMethodHandlesNothing()
    {
        var token = TestContext.CancellationToken;
        await using var connection = new HostProgressConnection(new ReplCore());
        var replies = await connection.Proxy.HandleRetainedSourceRunAsync(["ldc.i4.s 42", "ret"],
            [new AnalysisLocation("paste", 0, 0, 11), new AnalysisLocation("paste", 1, 0, 3)], token);
        Assert.IsEmpty(replies);
        Assert.IsFalse(connection.Progress.Last().IsRunning);
        Assert.IsEmpty(connection.Checkpoints);
        Assert.IsTrue((await connection.Proxy.HandleAsync("ldc.i4.s 42", token)).Succeeded);
        var result = await connection.Proxy.HandleAsync("ret", token);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
    }

    /// <summary>
    /// A pasted method body reaches the host in a handful of operations while every line is still answered.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Submission_SendsAMethodBodyInAHandfulOfHostOperations()
    {
        var token = TestContext.CancellationToken;
        var host = await HostPaths.StartEngineAsync(token);
        await using var controller = new SessionController(host, async ct => await HostPaths.StartEngineAsync(ct));
        var operations = new ConcurrentDictionary<string, bool>();
        host.ProgressChanged += progress => operations.TryAdd(progress.Identity, true);
        var events = new ConcurrentQueue<SubmissionEvent>();
        string[] source = [".method int32 Big() {", "  ldc.i4 7", .. Enumerable.Repeat("  nop", 196), "  ret", "}"];
        var submission = new Submission(controller, source, 0, false, _ => Task.CompletedTask, events.Enqueue);
        await submission.Completion.WaitAsync(token);
        Assert.AreEqual(SubmissionEventKind.Completed, events.Last().Kind);
        Assert.AreEqual(200, submission.Sent);
        Assert.HasCount(200, events);
        Assert.IsLessThan(16, operations.Count, "A run of instructions must not cost one host operation for each line.");
        Assert.IsTrue((await controller.HandleAsync("call int32 Big()", token)).Succeeded);
        var result = await controller.HandleAsync("ret", token);
        Assert.Contains(line => line.PlainText.Contains("= 7 : int32", StringComparison.Ordinal), result.Lines);
    }

    private static AnalysisLocation[] Locations(string[] source, int first) =>
        [.. Enumerable.Range(first, source.Length - first).Select(index => new AnalysisLocation("paste", index, 0, source[index].Length))];

    /// <summary>
    /// Recovery preserves accepted provisional lines together with unsent source and independently typed input.
    /// </summary>
    /// <param name="crash">Whether recovery follows an unexpected exit instead of an explicit restart.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task Recovery_RestoresEveryRetainedLineOnce(bool crash)
    {
        var token = TestContext.CancellationToken;
        var host = await HostPaths.StartEngineAsync(token);
        await using var controller = new SessionController(host, async ct => await HostPaths.StartEngineAsync(ct));
        var checkpoints = new ConcurrentQueue<SessionReply>();
        host.CheckpointReceived += checkpoints.Enqueue;
        string[] source = [".method int32 Answer() {", "ldc.i4.s 42", "nop", "nop", "ret", "}"];
        for (var index = 0; index < 3; index++)
        {
            controller.PendingInput = source[(index + 1)..];
            Assert.IsTrue((await controller.HandleSourceAsync(source[index],
                new AnalysisLocation("paste", index, 0, source[index].Length), token)).Succeeded);
        }
        var retained = Assert.ContainsSingle(checkpoints);
        Assert.AreSequenceEqual<string>([source[0]], retained.Document.Entries.SelectMany(entry => entry.Source));
        controller.QueuedInput = ["// queued"];
        controller.Editor = new SessionEditor { Lines = ["// typed"], Caret = 4, Anchor = 2 };
        var recovered = new TaskCompletionSource<SessionReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RecoveryCompleted += reply => recovered.TrySetResult(reply);
        if (crash)
        {
            using var process = Process.GetProcessById(host.ProcessId);
            process.Kill();
        }
        else await controller.RestartAsync(token);
        var workspace = await recovered.Task.WaitAsync(token);
        Assert.AreEqual(SessionRuntimeState.Ready, controller.RuntimeState);
        string[] expected = [.. source[1..], "// queued", "// typed"];
        Assert.AreSequenceEqual(expected, workspace.Document.Editor.Lines);
        Assert.AreSequenceEqual<string>([source[0]], workspace.Document.Entries.SelectMany(entry => entry.Source));
        Assert.AreEqual(string.Join('\n', expected[..^1]).Length + 5, controller.Editor.Caret);
        Assert.AreEqual(controller.Editor.Caret - 2, controller.Editor.Anchor);
        Assert.IsEmpty(workspace.Document.Interruptions, "No user code ran while collecting this method body.");
        controller.PendingInput = [];
        controller.Editor = new SessionEditor();
        foreach (var line in expected) Assert.IsTrue((await controller.HandleAsync(line, token)).Succeeded);
        Assert.IsTrue((await controller.HandleAsync("call int32 Answer()", token)).Succeeded);
        var result = await controller.HandleAsync("ret", token);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
    }

    /// <summary>
    /// Source changed after the initial acknowledgement forces a checkpoint instead of reusing a stale retained tail.
    /// </summary>
    /// <param name="changeCurrent">Whether the submitted instruction or the queued tail changes.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ChangedSource_ForcesFreshCheckpoint(bool changeCurrent)
    {
        var token = TestContext.CancellationToken;
        var host = await HostPaths.StartEngineAsync(token);
        await using var controller = new SessionController(host, async ct => await HostPaths.StartEngineAsync(ct));
        var checkpoints = new ConcurrentQueue<SessionReply>();
        host.CheckpointReceived += checkpoints.Enqueue;
        controller.PendingInput = ["nop", "nop", "ret", "}"];
        Assert.IsTrue((await controller.HandleSourceAsync(".method void Body() {", new AnalysisLocation("paste", 0, 0, 21), token))
            .Succeeded);
        Assert.HasCount(1, checkpoints);
        controller.PendingInput = changeCurrent ? ["nop", "ret", "}"] : ["ldc.i4.1", "pop", "ret", "}"];
        var line = changeCurrent ? "ldc.i4.1" : "nop";
        Assert.IsTrue((await controller.HandleSourceAsync(line, new AnalysisLocation("paste", 1, 0, line.Length), token)).Succeeded);
        Assert.HasCount(2, checkpoints);
        Assert.AreSequenceEqual<string>([".method void Body() {", line],
            checkpoints.Last().Document.Entries.SelectMany(entry => entry.Source));
    }

    /// <summary>
    /// Withdrawn source ends the retained batch, so a resubmitted tail acknowledges what the host actually holds.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Rollback_ForcesFreshCheckpoint()
    {
        var token = TestContext.CancellationToken;
        var host = await HostPaths.StartEngineAsync(token);
        await using var controller = new SessionController(host, async ct => await HostPaths.StartEngineAsync(ct));
        var checkpoints = new ConcurrentQueue<SessionReply>();
        host.CheckpointReceived += checkpoints.Enqueue;
        Assert.IsTrue((await controller.HandleAsync(".method void Body() {", token)).Succeeded);
        var mark = controller.Status.Mark;
        string[] source = ["nop", "ldc.i4.1", "pop", "ret"];
        for (var index = 0; index < 2; index++)
        {
            controller.PendingInput = source[(index + 1)..];
            Assert.IsTrue((await controller.HandleSourceAsync(source[index],
                new AnalysisLocation("paste", index, 0, source[index].Length), token)).Succeeded);
        }
        Assert.HasCount(2, checkpoints);
        await controller.RollbackAsync(mark, token);

        // The acknowledged revision still holds the withdrawn nop, and this tail matches what the frontend retained.
        Assert.IsTrue((await controller.HandleSourceAsync(source[1], new AnalysisLocation("paste", 0, 0, 8), token)).Succeeded);
        Assert.HasCount(3, checkpoints);
        var entries = checkpoints.Last().Document.Entries;
        Assert.AreEqual(SessionEntryKind.Rollback, entries[^2].Kind);
        Assert.AreSequenceEqual<string>([source[1]], entries[^1].Source);
    }

    /// <summary>
    /// A frontend restart in a pasted block retains accepted source without restoring the restart command itself.
    /// </summary>
    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task PastedRestart_CheckpointsPrecedingInstructions()
    {
        var token = TestContext.CancellationToken;
        var host = await HostPaths.StartEngineAsync(token);
        await using var controller = new SessionController(host, async ct => await HostPaths.StartEngineAsync(ct));
        var checkpoints = new ConcurrentQueue<SessionReply>();
        host.CheckpointReceived += checkpoints.Enqueue;
        string[] source = [".method int32 Answer() {", "ldc.i4.s 42", "nop", ".session restart", "ret", "}"];
        for (var index = 0; index < 3; index++)
        {
            controller.PendingInput = source[(index + 1)..];
            Assert.IsTrue((await controller.HandleSourceAsync(source[index],
                new AnalysisLocation("paste", index, 0, source[index].Length), token)).Succeeded);
        }
        Assert.HasCount(2, checkpoints);
        Assert.AreSequenceEqual(source[..3], checkpoints.Last().Document.Entries.SelectMany(entry => entry.Source));
        controller.PendingInput = source[4..];
        var restarted = await controller.HandleSourceAsync(source[3], new AnalysisLocation("paste", 3, 0, source[3].Length), token);
        Assert.IsTrue(restarted.Succeeded, string.Join('\n', restarted.Lines.Select(line => line.PlainText)));
        Assert.AreSequenceEqual(source[4..], controller.Editor.Lines);
        Assert.AreEqual(string.Join('\n', source[4..]).Length, controller.Editor.Caret);
        Assert.AreEqual(controller.Editor.Caret, controller.Editor.Anchor);
        controller.PendingInput = [];
        controller.Editor = new SessionEditor();
        foreach (var line in source[4..]) Assert.IsTrue((await controller.HandleAsync(line, token)).Succeeded);
        Assert.IsTrue((await controller.HandleAsync("call int32 Answer()", token)).Succeeded);
        var result = await controller.HandleAsync("ret", token);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
    }

    /// <summary>
    /// A refused retained instruction checkpoints the actual accepted source before reporting its error.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task RetainedError_PublishesCheckpoint()
    {
        var token = TestContext.CancellationToken;
        await using var connection = new HostProgressConnection(new ReplCore());
        Assert.IsTrue((await connection.Proxy.HandleAsync(".method void Body() {", token)).Succeeded);
        var reply = await connection.Proxy.HandleRetainedSourceAsync("ldc.i4 invalid", new AnalysisLocation("paste", 1, 0, 14), token);
        Assert.IsFalse(reply.Succeeded);
        Assert.HasCount(2, connection.Checkpoints);
        Assert.AreEqual(reply.Status, connection.Checkpoints.Last().Reply.Status);
        Assert.AreSequenceEqual<string>([".method void Body() {", "ldc.i4 invalid"],
            connection.Checkpoints.Last().Document.Entries.SelectMany(entry => entry.Source));
        Assert.AreEqual(SessionEntryKind.Rejected, connection.Checkpoints.Last().Document.Entries.Last().Kind);
        Assert.IsNull(connection.Checkpoints.Last().PendingSubmission);
        Assert.IsTrue((await connection.Proxy.HandleAsync("ret", token)).Succeeded);
        Assert.IsTrue((await connection.Proxy.HandleAsync("}", token)).Succeeded);
    }

    /// <summary>
    /// Even retained source cannot execute user IL before acknowledgement of its execution boundary.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task RetainedExecution_AwaitsCheckpointAndPublishesCommittedResult()
    {
        var token = TestContext.CancellationToken;
        await using var connection = new HostProgressConnection(new ReplCore());
        Assert.IsTrue((await connection.Proxy.HandleAsync("ldc.i4.s 42", token)).Succeeded);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.AcknowledgeCheckpoint = checkpoint =>
        {
            if (checkpoint.PendingSubmission is null) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        var pending = connection.Proxy.HandleRetainedSourceAsync("ret", new AnalysisLocation("paste", 1, 0, 3), token);
        try
        {
            await entered.Task.WaitAsync(token);
            Assert.IsFalse(pending.IsCompleted);
            Assert.DoesNotContain(progress => progress.Phase == ExecutionPhase.UserCode, connection.Progress);
            Assert.AreSequenceEqual<string>(["ldc.i4.s 42", "ret"], connection.Checkpoints.Last().PendingSource);
            release.TrySetResult();
            var reply = await pending;
            Assert.IsTrue(reply.Succeeded);
            Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), reply.Lines);
            Assert.IsNull(connection.Checkpoints.Last().PendingSubmission);
            Assert.AreEqual("succeeded", Assert.ContainsSingle(connection.Checkpoints.Last().Document.Cells).State);
        }
        finally
        {
            release.TrySetResult();
            await pending;
        }
    }
}
