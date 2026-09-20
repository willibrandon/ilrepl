using System.IO.Compression;
using System.Text;
using System.Text.Json;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Portable session files and share fragments preserve source while enforcing their structural and byte limits.
/// </summary>
[TestClass]
public sealed class SessionCodecTests
{
    /// <summary>
    /// A supported document can omit additive fields and retain the defaults of an empty editable workspace.
    /// </summary>
    [TestMethod]
    public void Read_OmittedAdditiveFieldsRetainDefaults()
    {
        var document = SessionCodec.Read("""{"format":"ilrepl-session","version":1}"""u8);

        Assert.IsEmpty(document.Entries);
        Assert.IsEmpty(document.Cells);
        Assert.IsEmpty(document.Interruptions);
        Assert.IsEmpty(document.References);
        Assert.IsEmpty(document.Assets);
        Assert.IsEmpty(document.Editor.Lines);
        Assert.AreEqual(0, document.Editor.Caret);
        Assert.IsNotNull(document.Runtime);
        var reopened = SessionCodec.Read(SessionCodec.Write(document));
        Assert.IsEmpty(reopened.Interruptions);
    }

    /// <summary>
    /// Format metadata can precede or follow the content without dropping later source or additive fields.
    /// </summary>
    /// <param name="metadataFirst">Whether both required properties precede the document content.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Read_PreservesContentRegardlessOfMetadataOrder(bool metadataFirst)
    {
        const string metadata = "\"format\":\"ilrepl-session\",\"version\":1";
        const string content = "\"editor\":{\"lines\":[\"// café λ\"]},\"future\":{\"version\":7,\"items\":[\"keep\"]}";
        var json = "{" + (metadataFirst ? metadata + "," + content : content + "," + metadata) + "}";

        var document = SessionCodec.Read(Encoding.UTF8.GetBytes(json));

        Assert.AreSequenceEqual(["// café λ"], document.Editor.Lines);
        Assert.IsNotNull(document.Extensions);
        Assert.AreEqual(7, document.Extensions["future"].GetProperty("version").GetInt32());
        Assert.AreEqual("keep", document.Extensions["future"].GetProperty("items")[0].GetString());
    }

    /// <summary>
    /// Finding the required metadata never bypasses full JSON parsing, final duplicate values, or source validation.
    /// </summary>
    /// <param name="json">The malformed document or unsupported final metadata value.</param>
    [TestMethod]
    [DataRow("{\"format\":\"ilrepl-session\",\"future\":{\"version\":1}}")]
    [DataRow("{\"version\":1,\"future\":{\"format\":\"ilrepl-session\"}}")]
    [DataRow("{\"format\":\"ilrepl-session\",\"version\":1,\"format\":\"other\"}")]
    [DataRow("{\"format\":\"ilrepl-session\",\"version\":1,\"version\":2}")]
    [DataRow("{\"format\":\"ilrepl-session\",\"version\":1}garbage")]
    [DataRow("{\"format\":\"ilrepl-session\",\"version\":1,\"editor\":{\"lines\":[\"two\\nlines\"]}}")]
    public void Read_MetadataPresenceDoesNotReplaceDocumentValidation(string json)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Read(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// Session JSON retains physical source, numbered historical results, assets, runtime facts, and nested additive fields.
    /// </summary>
    [TestMethod]
    public void RoundTrip_PreservesSourceHistoryAssetsAndAdditiveFields()
    {
        var document = Example();
        var bytes = SessionCodec.Write(document);
        using var json = JsonDocument.Parse(bytes);
        Assert.AreEqual(JsonValueKind.Array, json.RootElement.GetProperty("entries")[0].GetProperty("source").ValueKind);
        Assert.AreEqual("ilrepl-session", json.RootElement.GetProperty("format").GetString());
        Assert.AreEqual(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Contains("\n  \"format\"", Encoding.UTF8.GetString(bytes));

        var reopened = SessionCodec.Read(bytes);
        Assert.AreSequenceEqual(["  // café λ", "ldstr \"hello\\nworld\"", "", "ret"], reopened.Entries[0].Source);
        Assert.AreEqual("source-7", reopened.Entries[0].Identity);
        Assert.AreEqual(7, reopened.Cells[0].Number);
        Assert.AreEqual("cell-7", reopened.Cells[0].Identity);
        Assert.AreEqual("succeeded", reopened.Cells[0].State);
        Assert.AreSequenceEqual([".args (int32 n = 42)"], reopened.Cells[0].Inputs);
        Assert.AreEqual(LineKind.Result, reopened.Cells[0].Output[0].Kind);
        Assert.AreEqual("= 42 : int32", reopened.Cells[0].Output[0].PlainText);
        Assert.AreEqual(SpanStyle.Number, reopened.Cells[0].Output[0].Spans[0].Style);
        Assert.AreSequenceEqual(["  ldc.i4", ""], reopened.Editor.Lines);
        Assert.AreEqual(9, reopened.Editor.Caret);
        Assert.AreEqual(2, reopened.Editor.Anchor);
        Assert.AreEqual(17L, reopened.Editor.Revision);
        Assert.AreEqual("net10.0", reopened.Runtime.Framework);
        Assert.AreEqual("linux-x64", reopened.Runtime.Rid);
        Assert.AreEqual("en-US", reopened.Runtime.Culture);
        Assert.AreEqual("[1.2.3]", reopened.References[0].RequestedVersion);
        Assert.AreEqual("1.2.3", reopened.References[0].Version);
        Assert.AreEqual("lib/fixture.dll", reopened.References[0].Assets[0].Path);
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3, 4 }, reopened.Assets[0].Image);
        Assert.AreEqual("{\"version\":1}", reopened.PackageLock);
        foreach (var fields in new[]
        {
            reopened.Extensions, reopened.Runtime.Extensions, reopened.Editor.Extensions, reopened.Entries[0].Extensions,
            reopened.Cells[0].Extensions, reopened.References[0].Extensions, reopened.References[0].Assets[0].Extensions,
            reopened.Assets[0].Extensions,
        })
        {
            Assert.IsNotNull(fields);
            Assert.AreEqual("keep", fields["future"].GetProperty("value").GetString());
            Assert.AreEqual(2, fields["future"].GetProperty("items").GetArrayLength());
        }

        Assert.AreSequenceEqual(bytes, SessionCodec.Write(reopened));
    }

    /// <summary>
    /// Asset ordering is deterministic while a single image may serve several references.
    /// </summary>
    [TestMethod]
    public void Write_SortsAssetsAndAllowsSharedContentReferences()
    {
        var first = new SessionAsset { Image = [1], Hash = SessionCodec.Hash([1]) };
        var second = new SessionAsset { Image = [2], Hash = SessionCodec.Hash([2]) };
        var document = new SessionDocument
        {
            Assets = [first, second],
            References =
            [
                new() { Identity = "one", Request = "one.dll", Assets = [new() { Name = "one", Hash = first.Hash }] },
                new() { Identity = "two", Request = "two.dll", Assets = [new() { Name = "one", Hash = first.Hash }] },
            ],
        };

        var bytes = SessionCodec.Write(document);
        Assert.AreSequenceEqual(bytes, SessionCodec.Write(document with { Assets = [second, first] }));
        var reopened = SessionCodec.Read(bytes);
        Assert.HasCount(2, reopened.Assets);
        Assert.AreSequenceEqual(new[] { first.Hash, second.Hash }.Order(StringComparer.Ordinal),
            reopened.Assets.Select(asset => asset.Hash));
        Assert.AreEqual(reopened.References[0].Assets[0].Hash, reopened.References[1].Assets[0].Hash);
    }

    /// <summary>
    /// Empty documents and both ends of a valid editor selection remain representable.
    /// </summary>
    [TestMethod]
    public void RoundTrip_AcceptsEmptyDocumentAndSelectionEndpoints()
    {
        var empty = SessionCodec.Read(SessionCodec.Write(new()));
        Assert.IsEmpty(empty.Cells);
        Assert.IsEmpty(empty.Entries);
        Assert.IsEmpty(empty.Assets);
        Assert.IsEmpty(empty.Editor.Lines);

        var document = new SessionDocument { Editor = new() { Lines = ["a", "b"], Caret = 3, Anchor = 0 } };
        var selection = SessionCodec.Read(SessionCodec.Write(document)).Editor;
        Assert.AreEqual(3, selection.Caret);
        Assert.AreEqual(0, selection.Anchor);
        Assert.AreSequenceEqual(["a", "b"], selection.Lines);
    }

    /// <summary>
    /// Immutable edit originals retain their source, identities, baseline link, and additive snapshot fields.
    /// </summary>
    [TestMethod]
    public void RoundTrip_PreservesEditBaselineSnapshot()
    {
        var snapshot = EditSnapshot() with
        {
            Extensions = new() { ["future"] = JsonSerializer.SerializeToElement(new { retained = true }) },
            Original = MethodIdentityExample(),
            PinnedMethods = new() { ["Helper"] = MethodIdentityExample() with { Token = 0x06000002 } },
            SignatureHeaders = new() { ["Helper"] = ".method int32 Helper()" },
            TypeAliases = new() { ["Owner"] = "Example.Owner, Example" },
            MethodAliases = new() { ["PreviousEdit"] = MethodIdentityExample() with { Token = 0x06000003 } },
        };

        var document = EditDocument(snapshot);

        var reopened = SessionCodec.Read(SessionCodec.Write(document));

        var entry = reopened.Entries.Single();
        Assert.AreEqual(SessionEntryKind.Edit, entry.Kind);
        Assert.IsNotNull(entry.Edit);
        Assert.AreEqual("Changed", entry.Edit.Name);
        Assert.AreEqual("int32 Original()", entry.Edit.Reference);
        Assert.AreEqual("Original:module-identity:06000001", entry.Edit.Fingerprint);
        Assert.AreSequenceEqual([".method int32 Original() {", "  ldc.i4 42", "", "  ret", "}"], entry.Edit.Source);
        Assert.IsTrue(entry.Edit.OpensBlock);
        Assert.AreEqual("baseline", entry.Edit.BaselineReference);
        Assert.AreEqual("baseline", reopened.References.Single().Origin);
        Assert.IsNotNull(entry.Edit.Extensions);
        Assert.IsTrue(entry.Edit.Extensions["future"].GetProperty("retained").GetBoolean());
        Assert.IsNotNull(entry.Edit.Original);
        Assert.AreEqual("Example, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", entry.Edit.Original.Assembly);
        Assert.AreEqual("445f6172-8315-4661-a344-c234104d50f7", entry.Edit.Original.Module);
        Assert.AreEqual(0x06000001, entry.Edit.Original.Token);
        Assert.AreSequenceEqual([null, "System.String, System.Private.CoreLib"], entry.Edit.Original.TypeArguments);
        Assert.AreSequenceEqual(["System.Int32, System.Private.CoreLib", null], entry.Edit.Original.MethodArguments);
        Assert.IsNotNull(entry.Edit.Original.Extensions);
        Assert.AreEqual("preserved", entry.Edit.Original.Extensions["future"].GetString());
        Assert.AreEqual(0x06000002, entry.Edit.PinnedMethods["Helper"].Token);
        Assert.AreEqual(".method int32 Helper()", entry.Edit.SignatureHeaders["Helper"]);
        Assert.AreEqual("Example.Owner, Example", entry.Edit.TypeAliases["Owner"]);
        Assert.AreEqual(0x06000003, entry.Edit.MethodAliases["PreviousEdit"].Token);
    }

    /// <summary>
    /// Older supported edit snapshots remain readable without the additive frozen-method fields.
    /// </summary>
    [TestMethod]
    public void RoundTrip_AcceptsLegacyEditWithoutFrozenMethodIdentity()
    {
        var reopened = SessionCodec.Read(SessionCodec.Write(EditDocument(EditSnapshot())));
        var snapshot = reopened.Entries.Single().Edit;
        Assert.IsNotNull(snapshot);
        Assert.IsNull(snapshot.Original);
        Assert.IsEmpty(snapshot.PinnedMethods);
        Assert.IsEmpty(snapshot.SignatureHeaders);
        Assert.IsEmpty(snapshot.TypeAliases);
        Assert.IsEmpty(snapshot.MethodAliases);
    }

    /// <summary>
    /// Frozen method and alias metadata rejects malformed tokens, identities, generic arrays, and null dictionary contents.
    /// </summary>
    /// <param name="invalid">The malformed component.</param>
    [TestMethod]
    [DataRow("assembly-null")]
    [DataRow("assembly-empty")]
    [DataRow("module-null")]
    [DataRow("module-invalid")]
    [DataRow("token-zero")]
    [DataRow("token-row-zero")]
    [DataRow("token-type")]
    [DataRow("type-arguments-null")]
    [DataRow("type-argument-empty")]
    [DataRow("method-arguments-null")]
    [DataRow("method-argument-empty")]
    [DataRow("pinned-null")]
    [DataRow("pinned-key")]
    [DataRow("pinned-value")]
    [DataRow("headers-null")]
    [DataRow("header-key")]
    [DataRow("header-value")]
    [DataRow("types-null")]
    [DataRow("type-key")]
    [DataRow("type-value")]
    [DataRow("aliases-null")]
    [DataRow("alias-key")]
    [DataRow("alias-value")]
    public void Validate_RejectsInvalidFrozenMethodMetadata(string invalid)
    {
        var original = MethodIdentityExample();
        var identity = invalid switch
        {
            "assembly-null" => original with { Assembly = null! },
            "assembly-empty" => original with { Assembly = " " },
            "module-null" => original with { Module = null! },
            "module-invalid" => original with { Module = "not-a-module-guid" },
            "token-zero" => original with { Token = 0 },
            "token-row-zero" => original with { Token = 0x06000000 },
            "token-type" => original with { Token = 0x02000001 },
            "type-arguments-null" => original with { TypeArguments = null! },
            "type-argument-empty" => original with { TypeArguments = [""] },
            "method-arguments-null" => original with { MethodArguments = null! },
            "method-argument-empty" => original with { MethodArguments = ["\t"] },
            _ => original,
        };

        var valid = EditSnapshot() with { Original = identity };
        var snapshot = invalid switch
        {
            "pinned-null" => valid with { PinnedMethods = null! },
            "pinned-key" => valid with { PinnedMethods = new() { [""] = original } },
            "pinned-value" => valid with { PinnedMethods = new() { ["Helper"] = null! } },
            "headers-null" => valid with { SignatureHeaders = null! },
            "header-key" => valid with { SignatureHeaders = new() { [""] = ".method int32 Helper()" } },
            "header-value" => valid with { SignatureHeaders = new() { ["Helper"] = null! } },
            "types-null" => valid with { TypeAliases = null! },
            "type-key" => valid with { TypeAliases = new() { [""] = "Example.Owner, Example" } },
            "type-value" => valid with { TypeAliases = new() { ["Owner"] = null! } },
            "aliases-null" => valid with { MethodAliases = null! },
            "alias-key" => valid with { MethodAliases = new() { [""] = original } },
            "alias-value" => valid with { MethodAliases = new() { ["PreviousEdit"] = null! } },
            _ => valid,
        };

        Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Validate(EditDocument(snapshot)));
    }

    /// <summary>
    /// Distinct interrupted attempts retain source and additive fields even when they belong to the same prompt number.
    /// </summary>
    [TestMethod]
    public void RoundTrip_PreservesInterruptedAttempts()
    {
        var document = new SessionDocument
        {
            Interruptions =
            [
                new()
                {
                    Identity = "attempt-one",
                    Number = 7,
                    Source = ["  call Slow", "", "ret"],
                    Extensions = new() { ["future"] = JsonSerializer.SerializeToElement("retained") },
                },
                new() { Identity = "attempt-two", Number = 7, Source = [".compare Copy ()"] },
            ],
        };

        var file = SessionCodec.Read(SessionCodec.Write(document));
        var url = SessionCodec.Share(document, "https://example.test/demo");
        foreach (var reopened in new[] { file, SessionCodec.ReadFragment(url[url.IndexOf('#')..]) })
        {
            Assert.HasCount(2, reopened.Interruptions);
            Assert.AreEqual("attempt-one", reopened.Interruptions[0].Identity);
            Assert.AreEqual("attempt-two", reopened.Interruptions[1].Identity);
            Assert.AreEqual(7, reopened.Interruptions[0].Number);
            Assert.AreEqual(7, reopened.Interruptions[1].Number);
            Assert.AreSequenceEqual(["  call Slow", "", "ret"], reopened.Interruptions[0].Source);
            Assert.AreSequenceEqual([".compare Copy ()"], reopened.Interruptions[1].Source);
            var extensions = reopened.Interruptions[0].Extensions;
            Assert.IsNotNull(extensions);
            Assert.AreEqual("retained", extensions["future"].GetString());
        }
    }

    /// <summary>
    /// Interrupted attempts reject null components, duplicate identities, invalid prompt numbers, and malformed physical source.
    /// </summary>
    /// <param name="invalid">The malformed interruption component.</param>
    [TestMethod]
    [DataRow("collection-null")]
    [DataRow("item-null")]
    [DataRow("identity-null")]
    [DataRow("identity-empty")]
    [DataRow("duplicate")]
    [DataRow("number-zero")]
    [DataRow("number-negative")]
    [DataRow("source-null")]
    [DataRow("line-null")]
    [DataRow("line-newline")]
    [DataRow("line-return")]
    public void Validate_RejectsInvalidInterruptions(string invalid)
    {
        var original = new SessionInterruption { Identity = "attempt", Number = 7, Source = ["ret"] };
        var interruption = invalid switch
        {
            "item-null" => null,
            "identity-null" => original with { Identity = null! },
            "identity-empty" => original with { Identity = " " },
            "number-zero" => original with { Number = 0 },
            "number-negative" => original with { Number = -1 },
            "source-null" => original with { Source = null! },
            "line-null" => original with { Source = [null!] },
            "line-newline" => original with { Source = ["nop\nret"] },
            "line-return" => original with { Source = ["nop\rret"] },
            _ => original,
        };

        var document = new SessionDocument
        {
            Interruptions = invalid == "collection-null" ? null! : invalid == "duplicate" ? [original, original] : [interruption!],
        };

        Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Validate(document));
    }

    /// <summary>
    /// Interrupted source is included in the exact expanded-share and complete-file size limits.
    /// </summary>
    [TestMethod]
    public void Interruptions_RespectShareAndFileBoundaries()
    {
        var template = new SessionDocument { Interruptions = [new() { Identity = "attempt", Source = [""] }] };
        var overhead = SessionCodec.Write(template).Length;
        SessionDocument Sized(int limit) => template with
        {
            Interruptions = [template.Interruptions[0] with { Source = [new string('a', limit - overhead)] }],
        };

        var fragment = SessionCodec.Share(Sized(SessionCodec.FragmentDocumentLimit), "https://example.test/demo");
        var reopened = SessionCodec.ReadFragment(fragment[fragment.IndexOf('#')..]);
        Assert.AreEqual(SessionCodec.FragmentDocumentLimit - overhead, reopened.Interruptions[0].Source[0].Length);
        Assert.Contains("download", Assert.ThrowsExactly<InvalidDataException>(() =>
            SessionCodec.Share(Sized(SessionCodec.FragmentDocumentLimit + 1), "https://example.test/demo")).Message);
        Assert.HasCount(SessionCodec.FileLimit, SessionCodec.Write(Sized(SessionCodec.FileLimit)));
        Assert.Contains("64 MiB", Assert.ThrowsExactly<InvalidDataException>(() =>
            SessionCodec.Write(Sized(SessionCodec.FileLimit + 1))).Message);
    }

    /// <summary>
    /// Reopening and recapturing a foreign document preserves every recorded author-runtime fact.
    /// </summary>
    [TestMethod]
    public void Reopen_PreservesRecordedRuntimeMetadata()
    {
        var recorded = new SessionRuntime
        {
            IlreplVersion = "9.8.7", Framework = "net8.0", Description = ".NET 8.0.1", Rid = "osx-arm64",
            OperatingSystem = "author system", Architecture = "Arm64", Culture = "ja-JP",
        };

        var document = new SessionDocument { Runtime = recorded, Entries = [new() { Source = ["ldc.i4 42"] }] };
        using var core = new ReplCore();
        Assert.IsEmpty(core.ReopenSession(SessionCodec.Read(SessionCodec.Write(document))));

        var recaptured = SessionCodec.Read(SessionCodec.Write(core.CaptureSession(new SessionEditor())));

        Assert.AreEqual(recorded, recaptured.Runtime);
        Assert.AreSequenceEqual(["ldc.i4 42"], recaptured.Entries.Single().Source);
        Assert.IsTrue(core.Session.DeferActivation);
    }

    /// <summary>
    /// Edit transitions reject absent or malformed immutable metadata before any reconstruction can interpret it.
    /// </summary>
    /// <param name="invalid">The malformed snapshot component.</param>
    [TestMethod]
    [DataRow("missing")]
    [DataRow("name-null")]
    [DataRow("name-empty")]
    [DataRow("reference-null")]
    [DataRow("reference-empty")]
    [DataRow("fingerprint-null")]
    [DataRow("fingerprint-empty")]
    [DataRow("source-null")]
    [DataRow("source-line-null")]
    [DataRow("source-newline")]
    [DataRow("baseline-empty")]
    [DataRow("baseline-missing")]
    [DataRow("baseline-origin")]
    public void Validate_RejectsInvalidEditSnapshots(string invalid)
    {
        var original = EditSnapshot();
        var snapshot = invalid switch
        {
            "missing" => null,
            "name-null" => original with { Name = null! },
            "name-empty" => original with { Name = " " },
            "reference-null" => original with { Reference = null! },
            "reference-empty" => original with { Reference = "" },
            "fingerprint-null" => original with { Fingerprint = null! },
            "fingerprint-empty" => original with { Fingerprint = "\t" },
            "source-null" => original with { Source = null! },
            "source-line-null" => original with { Source = [null!] },
            "source-newline" => original with { Source = ["ldc.i4.1\nret"] },
            "baseline-empty" => original with { BaselineReference = "" },
            "baseline-missing" => original with { BaselineReference = "unknown" },
            _ => original,
        };

        var document = EditDocument(snapshot);
        if (invalid == "baseline-origin")
        {
            document = document with { References = [document.References.Single() with { Origin = "assembly" }] };
        }

        Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Validate(document));
    }

    /// <summary>
    /// Invalid JSON roots and absent or unsupported discriminators fail before a document can replace the workspace.
    /// </summary>
    /// <param name="json">The malformed or unsupported serialized document.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("{\"format\":\"ilrepl-session\"}")]
    [DataRow("{\"version\":1}")]
    [DataRow("{\"format\":\"other\",\"version\":1}")]
    [DataRow("{\"format\":\"ilrepl-session\",\"version\":0}")]
    [DataRow("{\"format\":\"ilrepl-session\",\"version\":2}")]
    [DataRow("{\"format\":\"ilrepl-session\",\"version\":null}")]
    public void Read_RejectsInvalidJsonAndUnsupportedFormats(string json)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Read(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// Required document components cannot be replaced with null by an imported file.
    /// </summary>
    /// <param name="property">The required document component.</param>
    [TestMethod]
    [DataRow("entries")]
    [DataRow("cells")]
    [DataRow("interruptions")]
    [DataRow("references")]
    [DataRow("assets")]
    [DataRow("editor")]
    [DataRow("runtime")]
    public void Read_RejectsNullDocumentComponents(string property)
    {
        var json = "{\"format\":\"ilrepl-session\",\"version\":1,\"" + property + "\":null}";
        var error = Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Read(Encoding.UTF8.GetBytes(json)));
        Assert.Contains("null", error.Message);
    }

    /// <summary>
    /// Invalid source identities, numbers, operations, and historical cell states are rejected consistently.
    /// </summary>
    /// <param name="problem">The violated document invariant.</param>
    [TestMethod]
    [DataRow("entry-null")]
    [DataRow("entry-identity")]
    [DataRow("entry-duplicate")]
    [DataRow("entry-number")]
    [DataRow("entry-kind")]
    [DataRow("cell-null")]
    [DataRow("cell-identity")]
    [DataRow("cell-duplicate")]
    [DataRow("cell-number-duplicate")]
    [DataRow("cell-number")]
    [DataRow("cell-kind")]
    [DataRow("cell-state")]
    [DataRow("cell-output-null")]
    [DataRow("output-line-null")]
    [DataRow("output-spans-null")]
    [DataRow("output-span-null")]
    [DataRow("output-text-null")]
    [DataRow("output-kind")]
    [DataRow("output-style")]
    public void Validate_RejectsInvalidSourceAndHistory(string problem)
    {
        var document = Example();
        var entry = document.Entries[0];
        var cell = document.Cells[0];
        var invalid = problem switch
        {
            "entry-null" => document with { Entries = [null!] },
            "entry-identity" => document with { Entries = [entry with { Identity = " " }] },
            "entry-duplicate" => document with { Entries = [entry, entry] },
            "entry-number" => document with { Entries = [entry with { Number = 0 }] },
            "entry-kind" => document with { Entries = [entry with { Kind = (SessionEntryKind)99 }] },
            "cell-null" => document with { Cells = [null!] },
            "cell-identity" => document with { Cells = [cell with { Identity = "" }] },
            "cell-duplicate" => document with { Cells = [cell, cell with { Number = 8 }] },
            "cell-number-duplicate" => document with { Cells = [cell, cell with { Identity = "another" }] },
            "cell-number" => document with { Cells = [cell with { Number = -1 }] },
            "cell-kind" => document with { Cells = [cell with { Kind = "unknown" }] },
            "cell-state" => document with { Cells = [cell with { State = "live" }] },
            "cell-output-null" => document with { Cells = [cell with { Output = null! }] },
            "output-line-null" => document with { Cells = [cell with { Output = [null!] }] },
            "output-spans-null" => document with { Cells = [cell with { Output = [new(LineKind.Result, null!)] }] },
            "output-span-null" => document with { Cells = [cell with { Output = [new(LineKind.Result, [null!])] }] },
            "output-text-null" => document with
                { Cells = [cell with { Output = [new(LineKind.Result, [new(null!, SpanStyle.Default)])] }] },
            "output-kind" => document with { Cells = [cell with { Output = [new((LineKind)99, [])] }] },
            _ => document with { Cells = [cell with { Output = [new(LineKind.Result, [new("value", (SpanStyle)99)])] }] },
        };

        Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Validate(invalid));
    }

    /// <summary>
    /// Every source-array owner rejects embedded line separators and null entries.
    /// </summary>
    /// <param name="owner">The source-array owner.</param>
    /// <param name="line">The invalid physical line.</param>
    [TestMethod]
    [DataRow("editor", "a\nb")]
    [DataRow("entry", "a\rb")]
    [DataRow("cell", "a\r\nb")]
    [DataRow("inputs", null)]
    public void Validate_RejectsInvalidPhysicalSourceLines(string owner, string? line)
    {
        var document = Example();
        var invalid = owner switch
        {
            "editor" => document with { Editor = document.Editor with { Lines = [line!] } },
            "entry" => document with { Entries = [document.Entries[0] with { Source = [line!] }] },
            "cell" => document with { Cells = [document.Cells[0] with { Source = [line!] }] },
            _ => document with { Cells = [document.Cells[0] with { Inputs = [line!] }] },
        };

        var error = Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Validate(invalid));
        Assert.Contains("physical lines", error.Message);
    }

    /// <summary>
    /// Editor selections cannot be negative or extend one character beyond their joined source.
    /// </summary>
    /// <param name="caret">The requested caret offset.</param>
    /// <param name="anchor">The requested selection anchor.</param>
    [TestMethod]
    [DataRow(-1, 0)]
    [DataRow(0, -1)]
    [DataRow(4, 0)]
    [DataRow(0, 4)]
    public void Validate_RejectsSelectionsOutsideSource(int caret, int anchor)
    {
        var document = new SessionDocument { Editor = new() { Lines = ["a", "b"], Caret = caret, Anchor = anchor } };
        var error = Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Validate(document));
        Assert.Contains("selection", error.Message);
    }

    /// <summary>
    /// Reference origins, graph links, asset descriptions, and their required collections are validated without loading code.
    /// </summary>
    /// <param name="problem">The violated dependency invariant.</param>
    [TestMethod]
    [DataRow("null")]
    [DataRow("identity")]
    [DataRow("duplicate")]
    [DataRow("request")]
    [DataRow("origin")]
    [DataRow("assets-null")]
    [DataRow("asset-null")]
    [DataRow("asset-name")]
    [DataRow("asset-kind")]
    [DataRow("asset-hash")]
    [DataRow("dependencies-null")]
    [DataRow("dependency-null")]
    [DataRow("dependency-missing")]
    [DataRow("frameworks-null")]
    [DataRow("framework-null")]
    [DataRow("entry-missing-reference")]
    public void Validate_RejectsInvalidReferences(string problem)
    {
        var document = Example();
        var reference = document.References[0];
        var asset = reference.Assets[0];
        var invalid = problem switch
        {
            "null" => document with { References = [null!] },
            "identity" => document with { References = [reference with { Identity = " " }] },
            "duplicate" => document with { References = [reference, reference] },
            "request" => document with { References = [reference with { Request = "" }] },
            "origin" => document with { References = [reference with { Origin = "unknown" }] },
            "assets-null" => document with { References = [reference with { Assets = null! }] },
            "asset-null" => document with { References = [reference with { Assets = [null!] }] },
            "asset-name" => document with { References = [reference with { Assets = [asset with { Name = "" }] }] },
            "asset-kind" => document with { References = [reference with { Assets = [asset with { Kind = "unknown" }] }] },
            "asset-hash" => document with { References = [reference with { Assets = [asset with { Hash = "bad" }] }] },
            "dependencies-null" => document with { References = [reference with { Dependencies = null! }] },
            "dependency-null" => document with { References = [reference with { Dependencies = [null!] }] },
            "dependency-missing" => document with { References = [reference with { Dependencies = ["absent"] }] },
            "frameworks-null" => document with { References = [reference with { Frameworks = null! }] },
            "framework-null" => document with { References = [reference with { Frameworks = [null!] }] },
            _ => document with { Entries = [new() { Kind = SessionEntryKind.Reference, Reference = "absent" }] },
        };

        Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Validate(invalid));
    }

    /// <summary>
    /// Embedded assets require unique canonical hashes that match their decoded bytes.
    /// </summary>
    /// <param name="problem">The invalid embedded-asset form.</param>
    [TestMethod]
    [DataRow("null")]
    [DataRow("image-null")]
    [DataRow("duplicate")]
    [DataRow("mismatch")]
    [DataRow("uppercase")]
    [DataRow("short")]
    public void Validate_RejectsInvalidEmbeddedAssets(string problem)
    {
        var document = Example();
        var asset = document.Assets[0];
        var invalid = document with
        {
            Assets = problem switch
            {
                "null" => [null!],
                "image-null" => [asset with { Image = null! }],
                "duplicate" => [asset, asset],
                "mismatch" => [asset with { Image = [9] }],
                "uppercase" => [asset with { Hash = asset.Hash.ToUpperInvariant() }],
                _ => [asset with { Hash = "abcd" }],
            },
        };

        Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Validate(invalid));
    }

    /// <summary>
    /// File imports accept their inclusive byte limit and refuse oversized input before JSON parsing.
    /// </summary>
    [TestMethod]
    public void Read_EnforcesFileLimitAtAndBeyondBoundary()
    {
        var bytes = new byte[SessionCodec.FileLimit + 1];
        bytes.AsSpan().Fill((byte)' ');
        SessionCodec.Write(new()).CopyTo(bytes, 0);
        Assert.AreEqual(1, SessionCodec.Read(bytes.AsSpan(0, SessionCodec.FileLimit - 1)).Version);
        Assert.AreEqual(1, SessionCodec.Read(bytes.AsSpan(0, SessionCodec.FileLimit)).Version);
        var error = Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Read(bytes));
        Assert.Contains("64 MiB", error.Message);
    }

    /// <summary>
    /// File serialization applies its inclusive byte limit after accounting for the JSON document overhead.
    /// </summary>
    [TestMethod]
    public void Write_EnforcesFileLimitAtAndBeyondBoundary()
    {
        var template = new SessionDocument { PackageLock = "" };
        var overhead = SessionCodec.Write(template).Length;
        var exact = template with { PackageLock = new string('a', SessionCodec.FileLimit - overhead) };
        Assert.HasCount(SessionCodec.FileLimit, SessionCodec.Write(exact));
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            SessionCodec.Write(exact with { PackageLock = exact.PackageLock + "a" }));
        Assert.Contains("64 MiB", error.Message);
    }

    /// <summary>
    /// Share fragments preserve source, the locked manifest, and required dependency images while replacing old fragments.
    /// </summary>
    [TestMethod]
    public void Share_PreservesSourceManifestAndRequiredAssets()
    {
        var document = Example();
        var url = SessionCodec.Share(document, "https://example.test/demo#old");
        Assert.StartsWith("https://example.test/demo#session=v1.", url);
        Assert.DoesNotContain("#old", url);
        var fragment = url[url.IndexOf('#')..];
        var reopened = SessionCodec.ReadFragment(fragment);
        Assert.AreSequenceEqual(SessionCodec.Write(document), SessionCodec.Write(reopened));
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3, 4 }, reopened.Assets[0].Image);
        Assert.AreEqual(document.References[0].Assets[0].Hash, reopened.References[0].Assets[0].Hash);
        Assert.AreEqual(document.PackageLock, reopened.PackageLock);
        Assert.AreEqual("cell-7", SessionCodec.ReadFragment(fragment[1..]).Cells[0].Identity);
    }

    /// <summary>
    /// Oversized required images cause an explicit download fallback while complete files retain their dependency bytes.
    /// </summary>
    [TestMethod]
    public void Share_LargeRequiredAssetsOfferDownloadWithoutSilentlyOmittingBytes()
    {
        var image = new byte[SessionCodec.FragmentDocumentLimit + 1];
        var hash = SessionCodec.Hash(image);
        var document = new SessionDocument
        {
            Entries = [new() { Source = ["ldc.i4 42", "ret"] }],
            Assets = [new() { Image = image, Hash = hash }],
            References = [new() { Request = "large.dll", Assets = [new() { Name = "large", Hash = hash }] }],
        };

        var error = Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Share(document, "https://example.test/demo"));

        Assert.Contains("download", error.Message);
        var reopened = SessionCodec.Read(SessionCodec.Write(document));
        Assert.AreEqual(hash, reopened.References.Single().Assets.Single().Hash);
        Assert.AreSequenceEqual(image, reopened.Assets.Single().Image);
    }

    /// <summary>
    /// The complete URL, including its page address, is accepted at the limit and refused one character beyond it.
    /// </summary>
    [TestMethod]
    public void Share_EnforcesCompleteUrlLimitAtBoundary()
    {
        var document = new SessionDocument();
        var suffix = SessionCodec.Share(document, "");
        var address = "https://example.test/";
        var baseUrl = address + new string('a', SessionCodec.LinkLimit - suffix.Length - address.Length);
        Assert.AreEqual(SessionCodec.LinkLimit - 1, SessionCodec.Share(document, baseUrl[..^1]).Length);
        Assert.AreEqual(SessionCodec.LinkLimit, SessionCodec.Share(document, baseUrl).Length);
        var error = Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Share(document, baseUrl + "a"));
        Assert.Contains("16 KiB", error.Message);
        Assert.Contains("download", error.Message);
    }

    /// <summary>
    /// Compressible documents are bounded by their expanded size on both generation and import.
    /// </summary>
    [TestMethod]
    public void Fragment_EnforcesExpandedLimitAtAndBeyondBoundary()
    {
        var template = new SessionDocument { PackageLock = "" };
        var overhead = SessionCodec.Write(template).Length;
        var exact = template with { PackageLock = new string('a', SessionCodec.FragmentDocumentLimit - overhead) };
        var below = exact with { PackageLock = exact.PackageLock![..^1] };
        Assert.HasCount(SessionCodec.FragmentDocumentLimit - 1, SessionCodec.Write(below));
        Assert.HasCount(SessionCodec.FragmentDocumentLimit, SessionCodec.Write(exact));
        Assert.AreEqual(below.PackageLock, SessionCodec.ReadFragment(Fragment(SessionCodec.Write(below))).PackageLock);
        Assert.AreEqual(exact.PackageLock, SessionCodec.ReadFragment(Fragment(SessionCodec.Write(exact))).PackageLock);
        Assert.Contains("#session=v1.", SessionCodec.Share(exact, "https://example.test/"));

        var over = exact with { PackageLock = exact.PackageLock + "a" };
        Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.Share(over, "https://example.test/"));
        var error = Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.ReadFragment(Fragment(SessionCodec.Write(over))));
        Assert.Contains("1 MiB", error.Message);
    }

    /// <summary>
    /// Unsupported fragment versions, invalid base64, and non-gzip bytes produce document errors.
    /// </summary>
    /// <param name="fragment">The invalid encoded fragment.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("session=v2.AA")]
    [DataRow("session=v1.%")]
    [DataRow("session=v1.a")]
    [DataRow("session=v1.AAAAAA")]
    public void ReadFragment_RejectsInvalidEncoding(string fragment)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.ReadFragment(fragment));
    }

    /// <summary>
    /// Incomplete gzip trailers and corrupt checksums cannot disguise an otherwise readable JSON document.
    /// </summary>
    /// <param name="damage">The missing or corrupt gzip bytes.</param>
    [TestMethod]
    [DataRow("trailer")]
    [DataRow("checksum")]
    [DataRow("length")]
    public void ReadFragment_RejectsTruncatedOrCorruptGzip(string damage)
    {
        var compressed = Compress(SessionCodec.Write(Example()));
        if (damage == "trailer")
        {
            compressed = compressed[..^8];
        }
        else
        {
            compressed[damage == "checksum" ? ^8 : ^4] ^= 1;
        }

        Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.ReadFragment(Encode(compressed)));
    }

    /// <summary>
    /// A long fragment is refused before the base64 or gzip decoders allocate an expanded document.
    /// </summary>
    [TestMethod]
    public void ReadFragment_RejectsOversizedEncodedInput()
    {
        var fragment = "session=v1." + new string('A', SessionCodec.LinkLimit);
        var error = Assert.ThrowsExactly<InvalidDataException>(() => SessionCodec.ReadFragment(fragment));
        Assert.Contains("share link", error.Message);
    }

    /// <summary>
    /// File classification recognizes only the complete recommended suffix without depending on path casing.
    /// </summary>
    /// <param name="path">The candidate file path.</param>
    /// <param name="expected">Whether it denotes a session document.</param>
    [TestMethod]
    [DataRow("example.ilrepl.json", true)]
    [DataRow("some folder/EXAMPLE.ILREPL.JSON", true)]
    [DataRow("example.json", false)]
    [DataRow("example.ilrepl.json.dll", false)]
    [DataRow("example.il", false)]
    [DataRow("", false)]
    public void IsSessionPath_RecognizesOnlySessionSuffix(string path, bool expected)
    {
        Assert.AreEqual(expected, SessionCodec.IsSessionPath(path));
    }

    /// <summary>
    /// Content identities use the canonical lowercase SHA-256 representation.
    /// </summary>
    [TestMethod]
    public void Hash_UsesCanonicalSha256()
    {
        Assert.AreEqual("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", SessionCodec.Hash([]));
        Assert.AreEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", SessionCodec.Hash("abc"u8));
    }

    private static SessionEditSnapshot EditSnapshot() => new()
    {
        Name = "Changed", Reference = "int32 Original()", Fingerprint = "Original:module-identity:06000001",
        Source = [".method int32 Original() {", "  ldc.i4 42", "", "  ret", "}"], OpensBlock = true, BaselineReference = "baseline",
    };

    private static SessionMethodIdentity MethodIdentityExample() => new()
    {
        Assembly = "Example, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
        Module = "445f6172-8315-4661-a344-c234104d50f7", Token = 0x06000001,
        TypeArguments = [null, "System.String, System.Private.CoreLib"],
        MethodArguments = ["System.Int32, System.Private.CoreLib", null],
        Extensions = new() { ["future"] = JsonSerializer.SerializeToElement("preserved") },
    };

    private static SessionDocument EditDocument(SessionEditSnapshot? snapshot) => new()
    {
        Entries = [new() { Kind = SessionEntryKind.Edit, Source = [".edit Changed int32 Original() {"], Edit = snapshot }],
        References = [new() { Identity = "baseline", Origin = "baseline", Request = "Original:module-identity:06000001" }],
    };

    private static SessionDocument Example()
    {
        int[] items = [1, 2];
        var fields = new Dictionary<string, JsonElement>
        {
            ["future"] = JsonSerializer.SerializeToElement(new { value = "keep", items }),
        };

        var hash = SessionCodec.Hash([1, 2, 3, 4]);
        return new SessionDocument
        {
            Runtime = new() { Framework = "net10.0", Rid = "linux-x64", Culture = "en-US", Extensions = fields },
            Entries =
            [
                new()
                {
                    Identity = "source-7",
                    Number = 7,
                    Source = ["  // café λ", "ldstr \"hello\\nworld\"", "", "ret"],
                    Extensions = fields,
                },
            ],
            Cells =
            [
                new()
                {
                    Identity = "cell-7",
                    Number = 7,
                    Source = ["ldarg n", "ret"],
                    Inputs = [".args (int32 n = 42)"],
                    State = "succeeded",
                    Output = [new(LineKind.Result, [new("= 42 : int32", SpanStyle.Number)])],
                    Extensions = fields,
                },
            ],
            Editor = new() { Lines = ["  ldc.i4", ""], Caret = 9, Anchor = 2, Revision = 17, Extensions = fields },
            References =
            [
                new()
                {
                    Identity = "reference-one",
                    Origin = "package",
                    Request = "Fixture",
                    RequestedVersion = "[1.2.3]",
                    Version = "1.2.3",
                    Assets = [new() { Name = "Fixture", Hash = hash, Path = "lib/fixture.dll", Extensions = fields }],
                    Extensions = fields,
                },
            ],
            Assets = [new() { Hash = hash, Image = [1, 2, 3, 4], Extensions = fields }],
            PackageLock = "{\"version\":1}",
            Extensions = fields,
        };
    }

    private static string Fragment(byte[] document) => Encode(Compress(document));

    private static string Encode(byte[] compressed) =>
        "session=v1." + Convert.ToBase64String(compressed).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Compress(byte[] bytes)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        return buffer.ToArray();
    }
}
