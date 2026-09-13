using System.Text.Json;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Checks source analysis identity, cancellation, and diagnostics at the engine and JSON boundaries.
/// </summary>
[TestClass]
public sealed class EngineAnalysisTests
{
    /// <summary>
    /// Supplies cancellation for asynchronous operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Caret movement reuses document facts while session changes invalidate their binding identity.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task CaretMoves_ReuseFactsAndResetInvalidatesThem()
    {
        await using var engine = new InProcessEngine();
        var lines = new[] { ".method int32 F() {", "ldc.i4.s 42", "ret", "}" };
        await engine.CompleteAsync(new CompletionRequest(["ld"], 0, 2, null, []), TestContext.CancellationToken);
        await engine.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 0), TestContext.CancellationToken);
        var first = await engine.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        var moved = await engine.AnalyzeAsync(new AnalysisRequest(lines, 2, 0, 2), TestContext.CancellationToken);
        Assert.AreEqual("[]", first.Stack!.Render());
        Assert.AreEqual("[int32]", moved.Stack!.Render());
        Assert.AreEqual(first.BindingEpoch, moved.BindingEpoch);
        Assert.AreEqual(2, moved.DocumentVersion);
        var header = await engine.AnalyzeAsync(new AnalysisRequest(lines, 0, 0, 3), TestContext.CancellationToken);
        Assert.IsNull(header.Stack, "A method declaration is not an instruction position.");
        await engine.HandleAsync(".reset", TestContext.CancellationToken);
        var reset = await engine.AnalyzeAsync(new AnalysisRequest(lines, 2, 0, 4), TestContext.CancellationToken);
        Assert.AreNotEqual(first.BindingEpoch, reset.BindingEpoch);
        Assert.AreEqual(engine.Status.Revision, reset.Revision);
    }

    /// <summary>
    /// Cancellation settles a large analysis without changing the live session or blocking a subsequent mutation.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task LargeAnalysis_CancelsWithoutHoldingTheInputGate()
    {
        await using var engine = new InProcessEngine();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var lines = Enumerable.Repeat("nop", 4000).Prepend(".method void Large() {").Append("}").ToArray();
        var running = engine.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => running);
        var handled = await engine.HandleAsync("ldc.i4.s 42", TestContext.CancellationToken);
        Assert.IsTrue(handled.Succeeded);
        Assert.IsNull(engine.Status.OpenMethod);
        Assert.AreEqual("[int32]", engine.Status.Stack);
    }

    /// <summary>
    /// Disposing an engine cancels and settles every analysis before releasing the session.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Dispose_SettlesAnalysisWorkers()
    {
        var engine = new InProcessEngine();
        var lines = Enumerable.Repeat("nop", 4000).Prepend(".method void Large() {").Append("}").ToArray();
        var running = engine.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        await engine.DisposeAsync();
        Assert.IsTrue(running.IsCompleted);
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// A late edge identifies the actual earlier instruction despite comments and a method header before it.
    /// </summary>
    [TestMethod]
    public async Task Rejection_RetainsEarlierDocumentLocation()
    {
        await using var engine = new InProcessEngine();
        var lines = new[] { ".method void F() {", "// comment", "br NEXT", "EARLIER: pop", "NEXT: nop", "br EARLIER" };
        HandleReply? reply = null;
        for (var index = 0; index < lines.Length; index++)
        {
            reply = await engine.HandleSourceAsync(lines[index], new AnalysisLocation("document", index, 0, lines[index].Length),
                TestContext.CancellationToken);
        }

        Assert.IsNotNull(reply);
        Assert.IsFalse(reply.Succeeded);
        var error = reply.Diagnostics.Single();
        Assert.AreEqual("document", error.Location.Body);
        Assert.AreEqual(3, error.Location.Line);
        var json = JsonSerializer.Serialize(reply, ProtocolJsonContext.Default.HandleReply);
        var restored = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.HandleReply)!;
        Assert.AreEqual(error.Location, restored.Diagnostics.Single().Location);
        Assert.HasCount(error.Related.Count, restored.Diagnostics.Single().Related);
    }

    /// <summary>
    /// Every presentation state retains its distinction and nullable depth through source-generated JSON.
    /// </summary>
    [TestMethod]
    [DataRow(AnalyzedStackKind.Known)]
    [DataRow(AnalyzedStackKind.Unknown)]
    [DataRow(AnalyzedStackKind.Invalid)]
    [DataRow(AnalyzedStackKind.Unreachable)]
    public void Analysis_RoundTripsStackKinds(AnalyzedStackKind kind)
    {
        var reply = new AnalysisReply(5, 6, 7, 8, new AnalyzedStack(kind, ["int32"], false), true,
            [new("FLOW003", AnalysisDiagnosticKind.Error, "incompatible stacks", new("F", 4, 2, 5),
                [new(new("F", 1, 0, 3), "producer")])]);
        var json = JsonSerializer.Serialize(reply, ProtocolJsonContext.Default.AnalysisReply);
        var restored = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.AnalysisReply)!;
        Assert.AreEqual(kind, restored.Stack!.Kind);
        Assert.AreEqual(kind == AnalyzedStackKind.Known ? 1 : (int?)null, restored.Stack.Depth);
        Assert.AreEqual(reply.Diagnostics[0].Related[0], restored.Diagnostics[0].Related[0]);
        Assert.AreEqual(reply.DocumentVersion, restored.DocumentVersion);
        Assert.AreEqual(reply.BindingEpoch, restored.BindingEpoch);
    }
}
