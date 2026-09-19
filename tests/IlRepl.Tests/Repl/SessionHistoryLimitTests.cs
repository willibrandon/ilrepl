using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Presentation limits retain exact styled history tails while complete journals remain recoverable and unexecuted.
/// </summary>
[TestClass]
public sealed class SessionHistoryLimitTests
{
    /// <summary>
    /// Supplies cancellation to real host reconstruction and file operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Every tail boundary preserves comments, rollback state, rejected input, interrupted headers, and styled output.
    /// </summary>
    [TestMethod]
    public void Tail_PreservesEveryStyledBoundary()
    {
        using var source = new ReplCore();
        Assert.IsTrue(source.Handle("/* beginning").Succeeded);
        Assert.IsFalse(source.Handle("end */ bogus /* rejected").Succeeded);
        Assert.IsTrue(source.Handle("ldc.i4 42").Succeeded);
        Assert.IsTrue(source.Handle("ret").Succeeded);
        var original = source.CaptureSession(new SessionEditor());
        var document = original with
        {
            Entries = [.. original.Entries,
                new SessionEntry { Number = 2, Kind = SessionEntryKind.Reset, Source = [".reset /* ignored"] },
                new SessionEntry { Number = 2, Source = ["ldc.i4.1"] },
                new SessionEntry { Number = 2, Kind = SessionEntryKind.Rollback,
                    Mark = SessionMark.Initial with { InBlockComment = true }, Source = ["// rollback"] },
                new SessionEntry { Number = 2, Source = ["restored comment */ ldc.i4.2"] }],
            Cells = [.. original.Cells.Select(cell => cell with { State = "interrupted" }),
                new SessionCell { Number = 3, Source = ["ldc.i4.3", "ret"],
                    Output = [TranscriptLine.Of(LineKind.Result, "  = 3 : int32", SpanStyle.Number)] }],
        };
        var complete = ReplCore.RenderSessionHistory(document);
        Assert.Contains(line => line.PlainText == "  1: cell, interrupted (historical)", complete);
        Assert.Contains(line => line.Spans.Any(span => span.Style == SpanStyle.Comment
            && span.Text.Contains("restored comment", StringComparison.Ordinal)), complete);
        for (var count = 1; count <= complete.Length + 1; count++)
        {
            AssertRows(complete.TakeLast(count).ToArray(), ReplCore.RenderSessionHistoryTail(document, count));
        }

        AssertRows(complete, ReplCore.RenderSessionHistoryTail(document, 0));
    }

    /// <summary>
    /// Empty workspaces produce no historical framing with either unlimited or one-row presentation capacity.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void Tail_EmptyHistoryRemainsEmpty(int maximumLines) =>
        Assert.IsEmpty(ReplCore.RenderSessionHistoryTail(new SessionDocument(), maximumLines));

    /// <summary>
    /// An invalid limit rejects hydration before changing the runtime, allowing a subsequent valid reconstruction.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Hydrate_NegativeLimitDoesNotReconstruct()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var document = files.CompletedDocument();
        File.Delete(files.MarkerPath);
        await using var engine = new InProcessEngine();
        var request = new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Hydrate }, Document = document,
            HistoryLineLimit = -1, AnnounceOpen = false,
        };
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => engine.SessionAsync(request, token));
        var untouched = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture },
        }, token);
        Assert.IsEmpty(untouched.Document.Entries);
        Assert.IsEmpty(untouched.Document.Cells);
        var restored = await engine.SessionAsync(request with { HistoryLineLimit = 1 }, token);
        Assert.AreSequenceEqual(SessionCodec.Write(document), SessionCodec.Write(restored.Document));
        AssertRows(ReplCore.RenderSessionHistory(document).TakeLast(1).ToArray(), restored.Reply.Lines.ToArray());
        Assert.IsFalse(File.Exists(files.MarkerPath), "Rejected and valid hydration must both leave historical code unexecuted.");
    }

    /// <summary>
    /// Actual host startup and subsequent opens honor the frontend limit while explicit unlimited requests preserve all history.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Controller_AppliesLimitBeforeStartupAndLaterOpen()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var document = files.CompletedDocument();
        await files.WriteAsync(document, token);
        File.Delete(files.MarkerPath);
        var complete = ReplCore.RenderSessionHistory(document);
        const int Limit = 5;
        await using var controller = new SessionController(async ct => await HostPaths.StartEngineAsync(ct),
            new SessionRequest { Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath } }, Limit);
        await controller.Initialization.WaitAsync(token);
        Assert.AreEqual(SessionRuntimeState.Ready, controller.RuntimeState);
        var opened = controller.Workspace!;
        AssertRows(complete.TakeLast(Limit).ToArray(), opened.Reply.Lines.Skip(1).ToArray());
        Assert.AreSequenceEqual(SessionCodec.Write(document), SessionCodec.Write(opened.Document));
        Assert.IsFalse(File.Exists(files.MarkerPath));

        var restarted = await controller.RestartAsync(token);
        Assert.IsTrue(restarted.Reply.Succeeded);
        Assert.AreSequenceEqual(SessionCodec.Write(document), SessionCodec.Write(restarted.Document));
        Assert.AreEqual(Limit, controller.HistoryLineLimit);
        Assert.IsFalse(File.Exists(files.MarkerPath));

        var repeated = await controller.HandleAsync(".session open " + LiteralParser.Escape(files.SessionPath), token);
        Assert.IsTrue(repeated.Succeeded);
        Assert.AreEqual(LineKind.Input, repeated.Lines[0].Kind);
        AssertRows(complete.TakeLast(Limit).ToArray(), repeated.Lines.Skip(2).ToArray());
        var unlimited = await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath }, HistoryLineLimit = 0,
        }, token);
        AssertRows(complete, unlimited.Reply.Lines.Skip(1).ToArray());
        Assert.AreSequenceEqual(SessionCodec.Write(document), SessionCodec.Write(unlimited.Document));
        Assert.IsFalse(File.Exists(files.MarkerPath), "Displaying limited or unlimited history must not replay its file side effect.");
    }

    private static void AssertRows(TranscriptLine[] expected, TranscriptLine[] actual)
    {
        Assert.HasCount(expected.Length, actual);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index].Kind, actual[index].Kind);
            Assert.AreSequenceEqual(expected[index].Spans, actual[index].Spans);
        }
    }
}
