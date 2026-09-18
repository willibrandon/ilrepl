using System.Text;
using System.Text.Json;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Exercises ordered source checkpoints through the same generated JSON contract as the actual host connection.
/// </summary>
[TestClass]
public sealed class SessionCheckpointStoreTests
{
    /// <summary>
    /// A real method retains every acknowledged source revision while each physical line crosses the connection only once.
    /// </summary>
    [TestMethod]
    public void EncodeApply_RealMethodAppendTransfersEachSourceLineOnce()
    {
        using var core = new ReplCore();
        var sender = new SessionCheckpointStore();
        var receiver = new SessionCheckpointStore();
        string[] source = [".method int32 Big() {", "ldc.i4.s 42", .. Enumerable.Repeat("nop", 196), "ret", "}"];
        SessionDocument? received = null;
        var transferred = 0;
        foreach (var line in source)
        {
            Assert.IsTrue(core.Handle(line).Succeeded);
            var document = core.CaptureSession(new SessionEditor());
            var preserved = SessionCodec.Write(document);
            var encoded = sender.Encode(new SessionReply { Document = document });
            Assert.AreEqual(1, encoded.Document.Entries.Sum(entry => entry.Source.Length));
            transferred += encoded.Document.Entries.Sum(entry => entry.Source.Length);
            var applied = receiver.Apply(RoundTrip(encoded));
            Assert.IsNotNull(applied);
            received = applied.Document;
            Assert.AreSequenceEqual(preserved, SessionCodec.Write(received));
            Assert.AreSequenceEqual(preserved, SessionCodec.Write(document), "Encoding must not truncate the retained source.");
        }

        Assert.AreEqual(200, transferred);
        Assert.IsNotNull(received);
        using var reopened = new ReplCore();
        Assert.IsEmpty(reopened.ReopenSession(received));
        Assert.IsTrue(reopened.Handle("call int32 Big()").Succeeded);
        Assert.IsTrue(reopened.Handle("ret").Succeeded);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), reopened.Transcript.Lines);
    }

    /// <summary>
    /// Source tails preserve replacement, deletion, reordering, and changed metadata without mutating either input snapshot.
    /// </summary>
    /// <param name="change">The source or metadata transition.</param>
    /// <param name="kept">The exact source prefix retained from the preceding entry.</param>
    [TestMethod]
    [DataRow("append", 3)]
    [DataRow("shrink", 1)]
    [DataRow("empty", 0)]
    [DataRow("replace", 0)]
    [DataRow("interior", 1)]
    [DataRow("reorder", 0)]
    [DataRow("identity", 0)]
    [DataRow("metadata", 3)]
    public void EncodeApply_SourceTailRetainsExactChangedEntry(string change, int kept)
    {
        var sender = new SessionCheckpointStore();
        var receiver = new SessionCheckpointStore();
        var entry = new SessionEntry { Source = ["ldc.i4.1", "ldc.i4.2", "add"] };
        var previous = new SessionDocument
        {
            Entries = [new SessionEntry { Source = ["// prefix"] }, entry, new SessionEntry { Source = ["ret"] }],
        };
        var accepted = receiver.Apply(RoundTrip(sender.Encode(new SessionReply { Document = previous })));
        Assert.IsNotNull(accepted);
        var changed = change switch
        {
            "append" => entry with { Source = [.. entry.Source, "nop"] },
            "shrink" => entry with { Source = [entry.Source[0]] },
            "empty" => entry with { Source = [] },
            "replace" => entry with { Source = ["ldc.i4.3", "ldc.i4.2", "add"] },
            "interior" => entry with { Source = ["ldc.i4.1", "ldc.i4.3", "add"] },
            "reorder" => entry with { Source = ["ldc.i4.2", "ldc.i4.1", "add"] },
            "identity" => entry with { Identity = "replacement" },
            "metadata" => entry with
            {
                Number = 2, Kind = SessionEntryKind.Rejected, Reference = "changed", Mark = SessionMark.Initial,
                Extensions = new() { ["future"] = JsonSerializer.SerializeToElement(42) },
            },
            _ => throw new ArgumentException("unknown transition", nameof(change)),
        };
        var current = previous with { Entries = [previous.Entries[0], changed, previous.Entries[2]] };
        var originalBytes = SessionCodec.Write(previous);
        var currentBytes = SessionCodec.Write(current);
        var encoded = sender.Encode(new SessionReply { Document = current });
        Assert.AreEqual(1, encoded.CheckpointDelta!.EntriesKept);
        Assert.AreEqual(kept, encoded.CheckpointDelta.SourceKept);
        Assert.AreSequenceEqual(changed.Source[kept..], encoded.Document.Entries[0].Source);
        var decoded = RoundTrip(encoded);
        var wireBytes = JsonSerializer.SerializeToUtf8Bytes(decoded, ProtocolJsonContext.Default.SessionReply);
        var result = receiver.Apply(decoded);
        Assert.IsNotNull(result);
        Assert.AreSequenceEqual(currentBytes, SessionCodec.Write(result.Document));
        Assert.AreSequenceEqual(currentBytes, SessionCodec.Write(current));
        Assert.AreSequenceEqual(originalBytes, SessionCodec.Write(previous));
        Assert.AreSequenceEqual(originalBytes, SessionCodec.Write(accepted.Document));
        Assert.AreSequenceEqual(wireBytes, JsonSerializer.SerializeToUtf8Bytes(decoded, ProtocolJsonContext.Default.SessionReply));
        Assert.IsNull(receiver.Apply(decoded));
    }

    /// <summary>
    /// Invalid source prefixes leave the sequence and retained source intact for the valid replacement revision.
    /// </summary>
    /// <param name="corruption">The malformed source-prefix relationship.</param>
    [TestMethod]
    [DataRow("negative")]
    [DataRow("too long")]
    [DataRow("negative entry")]
    [DataRow("missing predecessor")]
    [DataRow("missing change")]
    [DataRow("identity")]
    public void Apply_RejectsInvalidSourcePrefixesWithoutAdvancing(string corruption)
    {
        var sender = new SessionCheckpointStore();
        var receiver = new SessionCheckpointStore();
        var entry = new SessionEntry { Source = ["ldc.i4.1"] };
        var first = new SessionDocument { Entries = [entry] };
        var retained = receiver.Apply(RoundTrip(sender.Encode(new SessionReply { Document = first })));
        Assert.IsNotNull(retained);
        var second = first with { Entries = [entry with { Source = [.. entry.Source, "ret"] }] };
        var valid = RoundTrip(sender.Encode(new SessionReply { Document = second }));
        Assert.AreEqual(1, valid.CheckpointDelta!.SourceKept);
        var malformed = corruption switch
        {
            "negative" => valid with { CheckpointDelta = valid.CheckpointDelta with { SourceKept = -1 } },
            "too long" => valid with { CheckpointDelta = valid.CheckpointDelta with { SourceKept = 2 } },
            "negative entry" => valid with { CheckpointDelta = valid.CheckpointDelta with { EntriesKept = -1 } },
            "missing predecessor" => valid with { CheckpointDelta = valid.CheckpointDelta with { EntriesKept = 1 } },
            "missing change" => valid with { Document = valid.Document with { Entries = [] } },
            "identity" => valid with
            {
                Document = valid.Document with { Entries = [valid.Document.Entries[0] with { Identity = "unrelated" }] },
            },
            _ => throw new ArgumentException("unknown corruption", nameof(corruption)),
        };
        Assert.ThrowsExactly<InvalidDataException>(() => receiver.Apply(RoundTrip(malformed)));
        Assert.AreSequenceEqual(SessionCodec.Write(first), SessionCodec.Write(retained.Document));
        var recovered = receiver.Apply(valid);
        Assert.IsNotNull(recovered);
        Assert.AreSequenceEqual(SessionCodec.Write(second), SessionCodec.Write(recovered.Document));
    }

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

    private static SessionReply RoundTrip(SessionReply checkpoint) => JsonSerializer.Deserialize(
        JsonSerializer.SerializeToUtf8Bytes(checkpoint, ProtocolJsonContext.Default.SessionReply),
        ProtocolJsonContext.Default.SessionReply)!;
}
