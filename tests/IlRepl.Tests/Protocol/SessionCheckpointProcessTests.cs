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
