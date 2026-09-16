using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Reopening redisplays saved source and formatted output without evaluating code or consuming the restored editor.
/// </summary>
[TestClass]
public sealed class SessionHistoryTests
{
    /// <summary>
    /// Supplies cancellation for real engine and host operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Rejected commands remain visible as rejected history and cannot be mistaken for accepted source on reopen.
    /// </summary>
    [TestMethod]
    public async Task Hydrate_LabelsRejectedInputWithoutApplyingIt()
    {
        using var core = new ReplCore();
        Assert.IsFalse(core.Handle(".session bogus").Succeeded);
        Assert.IsTrue(core.Handle("ldc.i4 42").Succeeded);
        Assert.IsTrue(core.Handle("ret").Succeeded);
        var document = core.CaptureSession(new SessionEditor());
        Assert.AreEqual(SessionEntryKind.Rejected, document.Entries[0].Kind);
        await using var engine = new InProcessEngine();

        var opened = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Hydrate }, Document = document,
        }, TestContext.CancellationToken);

        var text = opened.Reply.Lines.Select(line => line.PlainText).ToArray();
        var rejected = Array.IndexOf(text, "il[1]> .session bogus");
        Assert.IsGreaterThan(0, rejected);
        Assert.AreEqual("  rejected input (not applied)", text[rejected - 1]);
        Assert.IsEmpty(opened.Diagnostics);
        Assert.AreSequenceEqual(SessionCodec.Write(document), SessionCodec.Write(opened.Document));
        Assert.AreEqual(2, engine.Status.CellNumber);
    }

    /// <summary>
    /// A failed run clears obsolete output for later unrun cells in saved history and cell listings.
    /// </summary>
    [TestMethod]
    public async Task Run_StopsWithoutPresentingStaleOutputForUnrunCells()
    {
        using var core = new ReplCore();
        foreach (var line in new[] { "ldc.i4.1", "ldc.i4.0", "div" }) Assert.IsTrue(core.Handle(line).Succeeded);
        Assert.IsFalse(core.Handle("ret").Succeeded);
        Assert.IsTrue(core.Handle("ldc.i4 42").Succeeded);
        Assert.IsTrue(core.Handle("ret").Succeeded);
        var document = core.CaptureSession(new SessionEditor());
        Assert.AreEqual("  = 42 : int32", Assert.ContainsSingle(document.Cells[1].Output).PlainText);
        await using var engine = new InProcessEngine();

        var ran = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Run }, Document = document,
        }, TestContext.CancellationToken);

        Assert.IsFalse(ran.Reply.Succeeded);
        Assert.AreEqual("unrun", ran.Document.Cells[1].State);
        Assert.IsEmpty(ran.Document.Cells[1].Output);
        Assert.AreEqual("  = 42 : int32", Assert.ContainsSingle(document.Cells[1].Output).PlainText);
        var listed = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Cells },
        }, TestContext.CancellationToken);
        Assert.Contains(line => line.PlainText == "  2: cell, unrun (historical)", listed.Reply.Lines);
        Assert.DoesNotContain(line => line.Kind == LineKind.Result, listed.Reply.Lines);
        Assert.DoesNotContain(line => line.Kind == LineKind.Result, ReplCore.RenderSessionHistory(ran.Document));
    }

    /// <summary>
    /// Local and hosted reopening redisplay exact input and styled output while preserving source, selection, and clean state.
    /// </summary>
    /// <param name="remote">Whether the history crosses the real host transport.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Hydrate_DisplaysSavedHistoryWithoutExecutionOrDocumentChanges(bool remote)
    {
        using var files = new SessionWorkspaceFixture();
        var editor = new SessionEditor { Lines = ["// unsent Ω draft", ""], Caret = 5, Anchor = 2, Revision = 17 };
        var document = files.CompletedDocument(editor);
        var saved = SessionCodec.Write(document);
        File.Delete(files.MarkerPath);
        await using var engine = remote ? await SessionWorkspaceFixture.StartAsync(TestContext.CancellationToken)
            : new SessionController(new InProcessEngine(), _ => Task.FromResult<IReplEngine>(new InProcessEngine()));

        var opened = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Hydrate }, Document = document,
        }, TestContext.CancellationToken);

        var lines = opened.Reply.Lines;
        Assert.AreSequenceEqual(document.Entries.SelectMany(entry => entry.Source.Select(source => $"il[{entry.Number}]> " + source)),
            lines.Where(line => line.Kind == LineKind.Input).Select(line => line.PlainText));
        Assert.AreSequenceEqual(document.Cells.SelectMany(cell => cell.Output).Select(line => line.PlainText),
            lines.Where(line => line.Kind is LineKind.Output or LineKind.Result or LineKind.Error).Select(line => line.PlainText));
        Assert.AreSequenceEqual(document.Cells[0].Output.SelectMany(line => line.Spans),
            lines.Where(line => line.Kind is LineKind.Output or LineKind.Result).SelectMany(line => line.Spans));
        Assert.Contains(line => line.PlainText == "  1: cell, succeeded (historical)", lines);
        Assert.AreEqual("  end of saved history; no code executed", lines[^1].PlainText);
        Assert.Contains(span => span.Style == SpanStyle.Prompt && span.Text == "il[1]> ", lines.SelectMany(line => line.Spans));
        Assert.DoesNotContain(line => line.PlainText.Contains("unsent Ω", StringComparison.Ordinal), lines);
        Assert.IsFalse(File.Exists(files.MarkerPath), "Redisplaying history executed the saved cell.");
        Assert.IsFalse(opened.Dirty);
        Assert.IsNotNull(opened.Reply.SessionEditor);
        Assert.AreSequenceEqual(editor.Lines, opened.Reply.SessionEditor.Lines);
        Assert.AreEqual(editor.Caret, opened.Reply.SessionEditor.Caret);
        Assert.AreEqual(editor.Anchor, opened.Reply.SessionEditor.Anchor);
        Assert.AreEqual(2, engine.Status.CellNumber);

        var captured = await engine.SessionAsync(new SessionRequest { Editor = engine.Editor }, TestContext.CancellationToken);
        Assert.AreSequenceEqual(saved, SessionCodec.Write(captured.Document));
        var inspection = await engine.HandleAsync(".session", TestContext.CancellationToken);
        Assert.DoesNotContain(line => line.Kind is LineKind.Input && line.PlainText.Contains("ldc.i4", StringComparison.Ordinal),
            inspection.Lines);
    }

    /// <summary>
    /// Definitions, withdrawn input, failed results, and unfinished accepted source retain their original order and prompt numbers.
    /// </summary>
    [TestMethod]
    public async Task Hydrate_DisplaysTransitionsFailureAndUnfinishedSourceInOrder()
    {
        using var core = new ReplCore();
        string[] source = ["/* saved comment", "   comment end */", ".method int32 Read() {", "ldc.i4 42", "ret", "}",
            "ldc.i4 99", ".clear", "call Read", "ret", ".reset", "ldc.i4.1", "ldc.i4.0", "div", "ret",
            ".method int32 Pending() {", "  ldc.i4.7"];
        foreach (var line in source) _ = core.Handle(line);
        var document = core.CaptureSession(new SessionEditor { Lines = ["ret", "}"] });
        Assert.AreSequenceEqual(["succeeded", "succeeded", "failed"], document.Cells.Select(cell => cell.State));
        await using var engine = new InProcessEngine();

        var reply = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Hydrate }, Document = document,
        }, TestContext.CancellationToken);

        Assert.IsEmpty(reply.Diagnostics);
        Assert.AreSequenceEqual(document.Entries.SelectMany(entry => entry.Source.Select(line => $"il[{entry.Number}]> " + line)),
            reply.Reply.Lines.Where(line => line.Kind == LineKind.Input).Select(line => line.PlainText));
        var text = reply.Reply.Lines.Select(line => line.PlainText).ToArray();
        Assert.IsLessThan(Array.IndexOf(text, "  = 42 : int32"), Array.IndexOf(text, "il[2]> ret"));
        Assert.IsLessThan(Array.IndexOf(text, "il[3]> .reset"), Array.IndexOf(text, "  = 42 : int32"));
        Assert.AreSequenceEqual(document.Cells[2].Output.SelectMany(line => line.Spans),
            reply.Reply.Lines.Where(line => line.Kind == LineKind.Error).SelectMany(line => line.Spans));
        Assert.Contains("  3: cell, failed (historical)", text);
        Assert.Contains("  4: saved input (historical)", text);
        Assert.AreEqual("Pending", engine.Status.OpenMethod);
        Assert.Contains(span => span.Style == SpanStyle.Comment && span.Text.Contains("comment end", StringComparison.Ordinal),
            reply.Reply.Lines.SelectMany(line => line.Spans));
    }

    /// <summary>
    /// Large histories are delivered once in full while the engine's ordinary transcript remains bounded.
    /// </summary>
    [TestMethod]
    public async Task Hydrate_DeliversHistoryBeyondEngineScrollbackLimit()
    {
        using var core = new ReplCore();
        core.Transcript.MaxLines = 2;
        await using var engine = new InProcessEngine(core);
        var document = new SessionDocument
        {
            Entries = Enumerable.Range(1, 2100).Select(index => new SessionEntry { Source = ["// source " + index] }).ToArray(),
        };

        var reply = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Hydrate }, Document = document,
        }, TestContext.CancellationToken);

        var input = reply.Reply.Lines.Where(line => line.Kind == LineKind.Input).ToArray();
        Assert.HasCount(2100, input);
        Assert.AreEqual("il[1]> // source 1", input[0].PlainText);
        Assert.AreEqual("il[1]> // source 2100", input[^1].PlainText);
        Assert.AreEqual(2, core.Transcript.MaxLines);
        Assert.IsEmpty(core.Transcript.Lines);
        Assert.IsFalse(reply.Dirty);
    }
}
