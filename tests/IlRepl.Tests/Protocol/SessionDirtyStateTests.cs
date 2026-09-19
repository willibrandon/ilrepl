using System.Text;
using System.Text.Json;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Compares actual saved snapshots with a reconstructed runtime without treating editor navigation as a content change.
/// </summary>
[TestClass]
public sealed class SessionDirtyStateTests
{
    /// <summary>
    /// Supplies cancellation to real engine operations and file writes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Acknowledging a written snapshot preserves every content distinction while ignoring navigation and embedded copies.
    /// </summary>
    /// <param name="change">The difference between the written snapshot and the current runtime.</param>
    /// <param name="dirty">Whether that difference changes the saved experiment.</param>
    [TestMethod]
    [DataRow("navigation", false)]
    [DataRow("empty-editor", false)]
    [DataRow("embedded-copy", false)]
    [DataRow("editor-text", true)]
    [DataRow("blank-lines", true)]
    [DataRow("source", true)]
    [DataRow("historical-output", true)]
    [DataRow("cell-state", true)]
    [DataRow("runtime", true)]
    [DataRow("extension", true)]
    [DataRow("reference", true)]
    [DataRow("package-lock", true)]
    [DataRow("interruption", true)]
    public async Task SavedSnapshot_DistinguishesContentFromPresentation(string change, bool dirty)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var editor = change is "empty-editor" or "blank-lines" ? new SessionEditor()
            : new SessionEditor { Lines = ["// café λ", ""], Caret = 3, Anchor = 1, Revision = 7 };
        var original = files.CompletedDocument(editor);
        File.Delete(files.MarkerPath);
        await using var engine = new InProcessEngine();
        var opened = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Hydrate, Path = files.SessionPath }, Document = original,
        }, token);

        Assert.IsFalse(opened.Dirty);
        var baseline = opened.Document;
        var output = baseline.Cells[0].Output;
        var written = change switch
        {
            "navigation" => baseline with { Editor = editor with { Caret = 6, Anchor = 2, Revision = 99 } },
            "empty-editor" => baseline with { Editor = editor with { Lines = [""] } },
            "embedded-copy" => baseline with { Assets = [new SessionAsset { Hash = SessionCodec.Hash([1, 2, 3]), Image = [1, 2, 3] }] },
            "editor-text" => baseline with { Editor = editor with { Lines = ["// café λ ", ""] } },
            "blank-lines" => baseline with { Editor = editor with { Lines = ["", ""] } },
            "source" => baseline with
            {
                Entries = [baseline.Entries[0] with
                    { Source = [.. baseline.Entries[0].Source, "// retained source"] }, .. baseline.Entries.Skip(1)],
            },
            "historical-output" => baseline with
            {
                Cells = [baseline.Cells[0] with
                    { Output = [.. output, TranscriptLine.Of(LineKind.Output, "retained output")] }],
            },
            "cell-state" => baseline with { Cells = [baseline.Cells[0] with { State = "failed" }] },
            "runtime" => baseline with { Runtime = baseline.Runtime with { Culture = "tr-TR" } },
            "extension" => baseline with { Extensions = new Dictionary<string, JsonElement> { ["future"] = FutureField() } },
            "reference" => baseline with { References = [new SessionReference { Request = "SavedReference" }] },
            "package-lock" => baseline with { PackageLock = "{\"version\":1}" },
            "interruption" => baseline with { Interruptions = [new SessionInterruption { Source = ["ret"], ExitCode = 17 }] },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        await files.WriteAsync(written, token);
        var bytes = await File.ReadAllBytesAsync(files.SessionPath, token);
        Assert.Contains("\n  \"format\"", Encoding.UTF8.GetString(bytes));
        var saved = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.AcknowledgeSave, Path = files.SessionPath },
            Document = SessionCodec.Read(bytes), Editor = editor,
        }, token);

        Assert.AreEqual(dirty, saved.Dirty, change);
        Assert.AreSequenceEqual(SessionCodec.Write(baseline), SessionCodec.Write(saved.Document));
        Assert.IsFalse(File.Exists(files.MarkerPath), "Saving and comparing source must not replay it.");

        await files.WriteAsync(baseline, token);
        var clean = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.AcknowledgeSave, Path = files.SessionPath },
            Document = SessionCodec.Read(await File.ReadAllBytesAsync(files.SessionPath, token)), Editor = editor,
        }, token);

        Assert.IsFalse(clean.Dirty, "Writing the actual current content restores the saved state.");
    }

    private static JsonElement FutureField()
    {
        using var json = JsonDocument.Parse("""{"text":"café λ","nested":[1,{"value":"retained"}]}""");
        return json.RootElement.Clone();
    }
}
