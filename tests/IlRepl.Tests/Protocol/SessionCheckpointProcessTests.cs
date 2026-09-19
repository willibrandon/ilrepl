using System.Collections.Concurrent;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Acknowledged real-host checkpoints preserve source and output without retransmitting presentation history.
/// </summary>
[TestClass]
public sealed class SessionCheckpointProcessTests
{
    /// <summary>
    /// Supplies cancellation to actual host processes and session files.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Queued cancellation and failed host operations leave later replies attached to their own acknowledged drafts.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ConcurrentCaptureAndFailures_PreserveMatchingAcknowledgedDocuments()
    {
        var token = TestContext.CancellationToken;
        await using var host = await HostPaths.StartEngineAsync(token);
        Assert.IsTrue((await host.HandleAsync("ldc.i4.s 42", token)).Succeeded);
        var checkpoints = new ConcurrentQueue<SessionReply>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var held = 0;
        host.CheckpointReceived += checkpoint =>
        {
            checkpoints.Enqueue(checkpoint);
            if (checkpoint.Document.Editor.Lines is ["// first"] && Interlocked.Exchange(ref held, 1) == 0)
            {
                entered.TrySetResult();
                release.Wait(token);
            }
        };
        var first = host.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture }, Editor = new SessionEditor { Lines = ["// first"] },
        }, token);
        try
        {
            await entered.Task.WaitAsync(token);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cancelled = host.SessionAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.Capture },
                Editor = new SessionEditor { Lines = ["// cancelled"] },
            }, cancellation.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        }
        finally
        {
            release.Set();
        }

        var firstReply = await first;
        Assert.AreEqual("// first", firstReply.Document.Editor.Lines[0]);
        Assert.AreSame(checkpoints.Last().Document, firstReply.Document);
        using var files = new SessionWorkspaceFixture();
        await Assert.ThrowsAsync<ReplEngineException>(() => host.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
        }, token));
        var captures = Enumerable.Range(0, 12).Select(index => host.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture },
            Editor = new SessionEditor { Lines = ["// draft " + index] },
        }, token)).ToArray();
        var replies = await Task.WhenAll(captures);
        for (var index = 0; index < replies.Length; index++)
        {
            var reply = replies[index];
            Assert.AreEqual("// draft " + index, reply.Document.Editor.Lines[0]);
            Assert.IsNull(reply.CheckpointDelivery);
            var acknowledged = checkpoints.Single(checkpoint =>
                checkpoint.Document.Editor.Lines.SequenceEqual(reply.Document.Editor.Lines));
            Assert.AreSame(acknowledged.Document, reply.Document);
            Assert.IsNull(acknowledged.CheckpointDelivery);
            Assert.AreEqual("ldc.i4.s 42", reply.Document.Entries.Single().Source.Single());
        }

        Assert.DoesNotContain(checkpoint => checkpoint.Document.Editor.Lines.Contains("// cancelled"), checkpoints);
        var executed = await host.HandleAsync("ret", token);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), executed.Lines);
    }

    /// <summary>
    /// Opening emits history once while checkpointed source reopens in another host without executing saved cells.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Open_CheckpointsRetainHistoryWithoutDisplayPayload()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var document = files.CompletedDocument(new SessionEditor { Lines = ["// unsent"] });
        await files.WriteAsync(document, token);
        File.Delete(files.MarkerPath);
        await using var host = await HostPaths.StartEngineAsync(token);
        var checkpoints = new ConcurrentQueue<SessionReply>();
        host.CheckpointReceived += checkpoints.Enqueue;
        var opened = await host.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
        }, token);
        Assert.IsTrue(opened.Reply.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("saved stdout", StringComparison.Ordinal), opened.Reply.Lines);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), opened.Reply.Lines);
        Assert.IsFalse(File.Exists(files.MarkerPath));
        Assert.IsNotEmpty(checkpoints);
        Assert.AreSame(checkpoints.Last().Document, opened.Document);
        Assert.IsNull(opened.CheckpointDelivery);
        foreach (var checkpoint in checkpoints)
        {
            Assert.IsEmpty(checkpoint.Reply.Lines, "Presentation history belongs to the operation reply, not its acknowledgement.");
            Assert.AreEqual(opened.Reply.Status, checkpoint.Reply.Status);
            Assert.AreEqual(opened.Path, checkpoint.Path);
            Assert.AreEqual(opened.Dirty, checkpoint.Dirty);
            Assert.AreSequenceEqual(SessionCodec.Write(opened.Document), SessionCodec.Write(checkpoint.Document));
        }

        var captured = await host.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture }, Editor = document.Editor,
        }, token);
        Assert.AreSequenceEqual(SessionCodec.Write(opened.Document), SessionCodec.Write(captured.Document));
        Assert.IsFalse(File.Exists(files.MarkerPath));
        await using var restored = await HostPaths.StartEngineAsync(token);
        var reopened = await restored.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Hydrate }, Document = checkpoints.Last().Document,
            AnnounceOpen = true,
        }, token);
        Assert.IsTrue(reopened.Reply.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("saved stdout", StringComparison.Ordinal), reopened.Reply.Lines);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), reopened.Reply.Lines);
        Assert.AreSequenceEqual(SessionCodec.Write(opened.Document), SessionCodec.Write(reopened.Document));
        Assert.IsFalse(File.Exists(files.MarkerPath), "Checkpoint reconstruction must retain history without replaying its side effect.");
    }
}
