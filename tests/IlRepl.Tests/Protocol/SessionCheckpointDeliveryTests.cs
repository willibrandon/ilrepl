using IlRepl.Processes;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Explicit request identities keep checkpoint documents correct across interleaved and abandoned operations.
/// </summary>
[TestClass]
public sealed class SessionCheckpointDeliveryTests
{
    /// <summary>
    /// Each reply resolves the document its own request acknowledged even after a different request publishes a newer workspace.
    /// </summary>
    [TestMethod]
    public void InterleavedAcknowledgements_ResolveTheirOwnDocuments()
    {
        using var core = new ReplCore();
        Assert.IsTrue(core.Handle("ldc.i4.s 42").Succeeded);
        var firstDocument = core.CaptureSession(new SessionEditor { Lines = ["// first draft"] });
        Assert.IsTrue(core.Handle("ret").Succeeded);
        var secondDocument = core.CaptureSession(new SessionEditor { Lines = ["// second draft"] });
        var deliveries = new SessionCheckpointDeliveries();
        var first = deliveries.Register();
        var second = deliveries.Register();
        deliveries.Remember(new SessionReply { CheckpointDelivery = first, Document = firstDocument });
        deliveries.Remember(new SessionReply { CheckpointDelivery = second, Document = secondDocument });
        var firstReply = deliveries.Resolve(new SessionReply { CheckpointDelivery = first }, first);
        var secondReply = deliveries.Resolve(new SessionReply { CheckpointDelivery = second }, second);
        Assert.AreSame(firstDocument, firstReply.Document);
        Assert.AreSame(secondDocument, secondReply.Document);
        Assert.IsEmpty(firstReply.Document.Cells);
        Assert.AreEqual("// first draft", firstReply.Document.Editor.Lines[0]);
        Assert.HasCount(1, secondReply.Document.Cells);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), secondReply.Document.Cells[0].Output);
        Assert.IsNull(firstReply.CheckpointDelivery);
        Assert.IsNull(secondReply.CheckpointDelivery);
        deliveries.Forget(first);
        deliveries.Forget(second);
    }

    /// <summary>
    /// Forgotten, unmatched, and unacknowledged deliveries cannot substitute another operation's workspace.
    /// </summary>
    [TestMethod]
    public void ForgottenAndUnmatchedDeliveries_AreRejected()
    {
        var deliveries = new SessionCheckpointDeliveries();
        var first = deliveries.Register();
        var second = deliveries.Register();
        using var core = new ReplCore();
        Assert.IsTrue(core.Handle("ldc.i4.s 42").Succeeded);
        var document = core.CaptureSession(new SessionEditor());
        var checkpoint = new SessionReply { CheckpointDelivery = first, Document = document };
        deliveries.Forget(first);
        deliveries.Remember(checkpoint);
        Assert.ThrowsExactly<HostProtocolException>(() => deliveries.Resolve(checkpoint, first));
        Assert.ThrowsExactly<HostProtocolException>(() => deliveries.Resolve(checkpoint, second));
        Assert.ThrowsExactly<HostProtocolException>(() => deliveries.Resolve(new SessionReply { CheckpointDelivery = second }, second));
        var complete = new SessionReply { Document = document };
        Assert.AreSame(complete, deliveries.Resolve(complete, second));
        deliveries.Forget(second);
    }
}
