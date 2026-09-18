using System.Text;
using System.Text.Json;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Exercises ordered source checkpoints through the same generated JSON contract as the actual host connection.
/// </summary>
[TestClass]
public sealed class SessionCheckpointStoreTests
{
    /// <summary>
    /// Changed tails, truncated history, editor state, and dependency bytes reconstruct the exact acknowledged document.
    /// </summary>
    [TestMethod]
    public void EncodeApply_RoundTripsChangedAndTruncatedRevisions()
    {
        var sender = new SessionCheckpointStore();
        var receiver = new SessionCheckpointStore();
        var image = Encoding.UTF8.GetBytes("checkpoint image");
        var first = new SessionDocument
        {
            Entries = [new SessionEntry { Source = ["ldc.i4.1"] }],
            Assets = [new SessionAsset { Hash = SessionCodec.Hash(image), Image = image }],
        };
        AssertRevision(first);
        var second = first with
        {
            Entries = [.. first.Entries, new SessionEntry { Source = ["ret"] }],
            Cells = [new SessionCell { Source = ["ldc.i4.1", "ret"], State = "succeeded" }],
            Editor = new SessionEditor { Lines = ["// draft"], Caret = 3, Anchor = 1 },
        };
        var append = AssertRevision(second);
        Assert.AreEqual(1, append.CheckpointDelta!.EntriesKept);
        Assert.HasCount(1, append.Document.Entries);
        Assert.IsEmpty(append.Document.Assets);
        var third = second with { Entries = [second.Entries[0]], Cells = [] };
        var truncate = AssertRevision(third);
        Assert.AreEqual(1, truncate.CheckpointDelta!.EntriesKept);
        Assert.AreEqual(0, truncate.CheckpointDelta.CellsKept);
        Assert.IsEmpty(truncate.Document.Entries);
        Assert.IsEmpty(truncate.Document.Cells);

        SessionReply AssertRevision(SessionDocument document)
        {
            var encoded = sender.Encode(new SessionReply { Document = document });
            var wire = JsonSerializer.SerializeToUtf8Bytes(encoded, ProtocolJsonContext.Default.SessionReply);
            var decoded = JsonSerializer.Deserialize(wire, ProtocolJsonContext.Default.SessionReply)!;
            var result = receiver.Apply(decoded);
            Assert.IsNotNull(result);
            Assert.AreSequenceEqual(SessionCodec.Write(document), SessionCodec.Write(result.Document));
            Assert.IsNull(receiver.Apply(decoded), "A duplicate checkpoint must never restore an older editor revision.");
            return encoded;
        }
    }

    /// <summary>
    /// A missing predecessor is rejected without damaging the receiver's ability to accept the next valid revision.
    /// </summary>
    [TestMethod]
    public void Apply_RejectsMissingAndInvalidPredecessorsWithoutAdvancing()
    {
        var receiver = new SessionCheckpointStore();
        var sender = new SessionCheckpointStore();
        var first = sender.Encode(new SessionReply());
        var second = sender.Encode(new SessionReply());
        Assert.ThrowsExactly<InvalidDataException>(() => receiver.Apply(second));
        Assert.ThrowsExactly<InvalidDataException>(() => receiver.Apply(first with
        {
            CheckpointDelta = first.CheckpointDelta! with { EntriesKept = 1 },
        }));
        Assert.IsNotNull(receiver.Apply(first));
        Assert.IsNotNull(receiver.Apply(second));
    }
}
