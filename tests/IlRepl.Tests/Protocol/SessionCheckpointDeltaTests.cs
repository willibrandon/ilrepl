using System.Text;
using System.Text.Json;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Incremental checkpoints preserve transport semantics across reconstructed and changed source records.
/// </summary>
[TestClass]
public sealed class SessionCheckpointDeltaTests
{
    /// <summary>
    /// Independently deserialized source, immutable edits, and styled history transfer no duplicate records or assets.
    /// </summary>
    [TestMethod]
    public void Create_ReconstructedEquivalentRecordsRemainIncremental()
    {
        var previous = Example();
        var current = SessionCodec.Read(SessionCodec.Write(previous));
        Assert.AreNotSame(previous.Entries[0], current.Entries[0]);
        Assert.AreNotSame(previous.Cells[0].Output[0].Spans, current.Cells[0].Output[0].Spans);
        var (delta, prefix) = SessionCheckpointDelta.Create(current, previous, new HashSet<string> { previous.Assets[0].Hash });

        Assert.AreEqual(2, prefix);
        Assert.IsEmpty(delta.Entries);
        Assert.IsEmpty(delta.Cells);
        Assert.IsEmpty(delta.Assets);
        Assert.AreSame(current.Editor, delta.Editor);
        Assert.AreSame(current.References, delta.References);
    }

    /// <summary>
    /// The first checkpoint transfers all data and a later append sends only the new source and previously unseen images.
    /// </summary>
    [TestMethod]
    public void Create_FirstAppendAndTruncationRetainExactSourceBoundaries()
    {
        var previous = Example();
        var first = SessionCheckpointDelta.Create(previous, null, new HashSet<string>());
        Assert.AreEqual(0, first.EntryPrefix);
        Assert.AreSequenceEqual(previous.Entries, first.Document.Entries);
        Assert.AreSequenceEqual(previous.Cells, first.Document.Cells);
        Assert.AreSequenceEqual(previous.Assets, first.Document.Assets);

        var current = SessionCodec.Read(SessionCodec.Write(previous));
        var appended = new SessionEntry { Identity = "appended", Number = 2, Source = ["ldc.i4.3"] };
        var image = Encoding.UTF8.GetBytes("another asset");
        var asset = new SessionAsset { Hash = SessionCodec.Hash(image), Image = image };
        current = current with { Entries = [.. current.Entries, appended], Assets = [.. current.Assets, asset] };
        var next = SessionCheckpointDelta.Create(current, previous, new HashSet<string> { previous.Assets[0].Hash });
        Assert.AreEqual(2, next.EntryPrefix);
        Assert.AreSequenceEqual([appended], next.Document.Entries);
        Assert.IsEmpty(next.Document.Cells);
        Assert.AreSequenceEqual([asset], next.Document.Assets);

        var truncated = SessionCheckpointDelta.Create(previous with { Entries = [previous.Entries[0]] }, current,
            current.Assets.Select(item => item.Hash).ToHashSet());
        Assert.AreEqual(1, truncated.EntryPrefix);
        Assert.IsEmpty(truncated.Document.Entries);
        Assert.IsEmpty(truncated.Document.Cells);
    }

    /// <summary>
    /// Stable identities never hide a changed source line, transition, immutable original, or additive field.
    /// </summary>
    [TestMethod]
    [DataRow("source")]
    [DataRow("kind")]
    [DataRow("mark")]
    [DataRow("reference")]
    [DataRow("extension")]
    [DataRow("edit-source")]
    [DataRow("original")]
    [DataRow("method-alias")]
    [DataRow("type-alias")]
    [DataRow("header")]
    public void Create_ChangedEntryContentsResendTheAffectedSuffix(string change)
    {
        var previous = Example();
        var current = SessionCodec.Read(SessionCodec.Write(previous));
        var entry = current.Entries[1];
        switch (change)
        {
            case "source": entry.Source = [".edit Changed {"]; break;
            case "kind": entry.Kind = SessionEntryKind.Rejected; break;
            case "mark": entry.Mark = SessionMark.Initial with { InBlockComment = true }; break;
            case "reference": entry.Reference = "Changed"; break;
            case "extension": entry.Extensions!["future"] = JsonSerializer.SerializeToElement(2); break;
            case "edit-source": entry.Edit!.Source = ["ldc.i4.7", "ret"]; break;
            case "original": entry.Edit!.Original!.TypeArguments = ["System.Int32"]; break;
            case "method-alias": entry.Edit!.MethodAliases["Alias"].Token++; break;
            case "type-alias": entry.Edit!.TypeAliases["Type"] = "System.Int32"; break;
            case "header": entry.Edit!.SignatureHeaders["Method"] = "int64 Method()"; break;
        }

        var (delta, prefix) = SessionCheckpointDelta.Create(current, previous, new HashSet<string> { previous.Assets[0].Hash });
        Assert.AreEqual(1, prefix, change);
        Assert.AreSequenceEqual([entry], delta.Entries, change);
        Assert.IsEmpty(delta.Cells);
        Assert.AreEqual(previous.Entries[1].Identity, entry.Identity);
    }

    /// <summary>
    /// Reconstructed cell history transfers changes to status, declarations, output, style, and additive fields.
    /// </summary>
    [TestMethod]
    [DataRow("state")]
    [DataRow("source")]
    [DataRow("inputs")]
    [DataRow("output")]
    [DataRow("style")]
    [DataRow("kind")]
    [DataRow("extension")]
    public void Create_ChangedCellContentsTransferOnlyThatCell(string change)
    {
        var previous = Example();
        var current = SessionCodec.Read(SessionCodec.Write(previous));
        var cell = current.Cells[0];
        switch (change)
        {
            case "state": cell.State = "failed"; break;
            case "source": cell.Source = ["ldc.i4.7", "ret"]; break;
            case "inputs": cell.Inputs = [".args (int32 n = 7)"]; break;
            case "output": cell.Output = [TranscriptLine.Of(LineKind.Result, "= 7", SpanStyle.Number)]; break;
            case "style": cell.Output = [TranscriptLine.Of(LineKind.Result, "= 42", SpanStyle.Error)]; break;
            case "kind": cell.Kind = "definition"; break;
            case "extension": cell.Extensions!["future"] = JsonSerializer.SerializeToElement(2); break;
        }

        var (delta, prefix) = SessionCheckpointDelta.Create(current, previous, new HashSet<string> { previous.Assets[0].Hash });
        Assert.AreEqual(2, prefix);
        Assert.IsEmpty(delta.Entries);
        Assert.AreSequenceEqual([cell], delta.Cells, change);
        Assert.AreEqual(previous.Cells[0].Identity, cell.Identity);
        Assert.IsEmpty(delta.Assets);
    }

    private static SessionDocument Example()
    {
        var method = new SessionMethodIdentity
        {
            Assembly = "Fixture", Module = Guid.NewGuid().ToString(), Token = 0x06000001,
            TypeArguments = ["System.String", null], MethodArguments = ["System.Int32"],
            Extensions = new() { ["future"] = JsonSerializer.SerializeToElement(1) },
        };

        var edit = new SessionEditSnapshot
        {
            Name = "Example", Reference = "int32 Fixture::Method()", Fingerprint = "original", Source = ["ldc.i4.s 42", "ret"],
            OpensBlock = true, Original = method, PinnedMethods = new() { ["Method"] = method },
            MethodAliases = new() { ["Alias"] = method }, TypeAliases = new() { ["Type"] = "System.String" },
            SignatureHeaders = new() { ["Method"] = "int32 Method()" },
        };

        var image = Encoding.UTF8.GetBytes("verified asset");
        return new SessionDocument
        {
            Entries =
            [
                new SessionEntry { Identity = "source", Source = ["// retained source"] },
                new SessionEntry
                {
                    Identity = "edit",
                    Kind = SessionEntryKind.Edit,
                    Source = [".edit Example {"],
                    Edit = edit,
                    Mark = SessionMark.Initial,
                    Extensions = new() { ["future"] = JsonSerializer.SerializeToElement(1) },
                },
            ],
            Cells = [new SessionCell
            {
                Identity = "cell",
                Source = ["ldc.i4.s 42", "ret"],
                State = "succeeded",
                Inputs = [".args (int32 n = 42)"],
                Output = [TranscriptLine.Of(LineKind.Result, "= 42", SpanStyle.Number)],
                Extensions = new() { ["future"] = JsonSerializer.SerializeToElement(1) },
            }],
            Assets = [new SessionAsset { Hash = SessionCodec.Hash(image), Image = image }],
            Editor = new SessionEditor { Lines = ["// draft"], Caret = 2, Anchor = 1 },
        };
    }
}
