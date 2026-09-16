using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Session reconstruction preserves accepted comment boundaries and reports only newly executed work.
/// </summary>
[TestClass]
public sealed class SessionLexicalStateTests
{
    /// <summary>
    /// Rejecting code preserves its preceding comment closure and discards any new comment opened by the refused code.
    /// </summary>
    /// <param name="rejected">The invalid source following a completed comment.</param>
    [TestMethod]
    [DataRow("end */ bogus")]
    [DataRow("end */ bogus /* refused comment")]
    public void AddLine_RejectionKeepsPrecedingCommentClosed(string rejected)
    {
        using var core = new ReplCore();
        var session = core.Session;
        session.AddLine("/* beginning");

        Assert.ThrowsExactly<ReplException>(() => session.AddLine(rejected));

        Assert.IsFalse(session.InBlockComment);
        session.AddLine("ldc.i4 42");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Rejected source remains historical while its comment closure survives live input, reopening, recall, and explicit replay.
    /// </summary>
    /// <param name="rejected">The invalid source following a completed comment.</param>
    [TestMethod]
    [DataRow("end */ bogus")]
    [DataRow("end */ .session bogus /* refused comment")]
    public void RejectedCommentClosure_SurvivesAllSessionPaths(string rejected)
    {
        using var source = new ReplCore();
        Submit(source, "/* beginning");
        Assert.IsFalse(source.Handle(rejected).Succeeded);
        Assert.IsFalse(source.Session.InBlockComment);
        Submit(source, "ldc.i4 42", "ret");
        var document = SessionCodec.Read(SessionCodec.Write(source.CaptureSession(new SessionEditor())));

        using var reopened = new ReplCore();
        Assert.IsEmpty(reopened.ReopenSession(document));

        Assert.IsFalse(reopened.Session.InBlockComment);
        Assert.AreEqual(2, reopened.CellNumber);
        Assert.IsEmpty(reopened.Transcript.Lines);
        Assert.AreSequenceEqual([rejected], document.Entries.Single(entry => entry.Kind == SessionEntryKind.Rejected).Source);
        Assert.AreSequenceEqual(["/* beginning", "end */", "ldc.i4 42", "ret"], Assert.ContainsSingle(document.Cells).Source);
        Assert.AreSequenceEqual(SessionCodec.Write(document), SessionCodec.Write(reopened.CaptureSession(new SessionEditor())));
        var history = ReplCore.RenderSessionHistory(document);
        var instruction = history.Single(line => line.PlainText == "il[1]> ldc.i4 42");
        Assert.Contains(span => span.Style == SpanStyle.Opcode && span.Text == "ldc.i4", instruction.Spans);
        var rejectedIndex = Array.FindIndex(history, line => line.PlainText == "il[1]> " + rejected);
        Assert.AreEqual("  rejected input (not applied)", history[rejectedIndex - 1].PlainText);
        using var recalled = new ReplCore();
        Submit(recalled, reopened.RecallSessionCell(document.Cells[0]));
        Assert.AreEqual("  = 42 : int32", Assert.ContainsSingle(Results(recalled)));
        using var replay = new ReplCore();
        Assert.IsTrue(replay.RunSession(document, [], CancellationToken.None).Succeeded, Transcript(replay));
        Assert.AreSequenceEqual(["  = 42 : int32"], Results(replay));
        Assert.IsFalse(replay.Session.InBlockComment);
    }

    /// <summary>
    /// A recorded run consumes its raw comment transition before following source or a current editor draft is reconstructed.
    /// </summary>
    /// <param name="opensComment">Whether the run opens the next comment rather than closing the preceding one.</param>
    /// <param name="selected">Whether only the second cell is selected for explicit execution.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void RunBoundary_RetainsRawCommentTransitions(bool opensComment, bool selected)
    {
        using var source = new ReplCore();
        string[] input = opensComment
            ? ["ldc.i4 21", "ret /* next", "end */ ldc.i4 42"]
            : ["ldc.i4 21", "/* beginning", "end */ ret", "ldc.i4 42"];
        Submit(source, input);
        var document = SessionCodec.Read(SessionCodec.Write(source.CaptureSession(new SessionEditor { Lines = ["ret"] })));
        using var reopened = new ReplCore();

        Assert.IsEmpty(reopened.ReopenSession(document));

        Assert.IsFalse(reopened.Session.InBlockComment);
        Assert.AreEqual(1, reopened.Status.Instructions);
        Assert.IsEmpty(reopened.Transcript.Lines);
        Assert.AreSequenceEqual(input, document.Entries.SelectMany(entry => entry.Source));
        Submit(reopened, "ret");
        Assert.AreSequenceEqual(["  = 42 : int32"], Results(reopened));
        using var replay = new ReplCore();
        Assert.IsTrue(replay.RunSession(document, selected ? [2] : [], CancellationToken.None).Succeeded, Transcript(replay));
        Assert.AreSequenceEqual(selected ? ["  = 42 : int32"] : ["  = 21 : int32", "  = 42 : int32"], Results(replay));
        Assert.AreEqual(3, replay.CellNumber);
        Assert.IsFalse(replay.Session.InBlockComment);
        var captured = replay.CaptureSession(new SessionEditor());
        Assert.AreSequenceEqual(document.Entries.Select(entry => entry.Identity),
            captured.Entries.Take(document.Entries.Length).Select(entry => entry.Identity));
        Assert.AreSequenceEqual(input.Concat(["ret"]), captured.Entries.SelectMany(entry => entry.Source));
    }

    /// <summary>
    /// Historical undo and clear transitions never appear as live feedback, including reconstruction after a failed cell.
    /// </summary>
    /// <param name="fails">Whether the first cell fails and later input is reconstructed without executing it.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RunSession_ReconstructsUndoAndClearQuietly(bool fails)
    {
        using var source = new ReplCore();
        Submit(source, ".method int32 Abandoned() {", "ldc.i4 99", ".clear", ".method int32 Withdrawn() {", ".undo",
            ".class public Discarded {", ".clear", "ldc.i4 99", ".undo", "ldc.i4 88", ".clear");
        Submit(source, fails ? ["ldc.i4.1", "ldc.i4.0", "div"] : ["ldc.i4 42"]);
        Assert.AreEqual(!fails, source.Handle("ret").Succeeded);
        Submit(source, "ldc.i4 77", ".undo", "ldc.i4 66", ".clear", "ldc.i4 43", "ret");
        var document = source.CaptureSession(new SessionEditor());
        using var replay = new ReplCore();
        var observed = new List<TranscriptLine>();
        replay.Transcript.LineAdded += observed.Add;

        var result = replay.RunSession(document, [], CancellationToken.None);

        Assert.AreEqual(!fails, result.Succeeded);
        Assert.AreSequenceEqual(fails
            ? ["  running cell 1 from the saved source", "  stopped at cell 1; all source remains available"]
            : ["  running cell 1 from the saved source", "  running cell 2 from the saved source"],
            observed.Where(line => line.Kind == LineKind.Info).Select(line => line.PlainText));
        Assert.DoesNotContain(line => line.Kind == LineKind.Stack, observed);
        string[] expected = fails ? [] : ["  = 42 : int32", "  = 43 : int32"];
        Assert.AreSequenceEqual(expected, Results(replay));
        Assert.IsEmpty(replay.Session.Methods);
        Assert.AreEqual(0, replay.Session.TypeCount);
        Assert.AreSequenceEqual(document.Entries.Select(entry => entry.Identity),
            replay.CaptureSession(new SessionEditor()).Entries.Select(entry => entry.Identity));
    }

    private static void Submit(ReplCore core, params string[] lines)
    {
        foreach (var line in lines) Assert.IsTrue(core.Handle(line).Succeeded, line + "\n" + Transcript(core));
    }

    private static string[] Results(ReplCore core) => core.Transcript.Lines.Where(line => line.Kind == LineKind.Result)
        .Select(line => line.PlainText).ToArray();

    private static string Transcript(ReplCore core) => string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText));
}
