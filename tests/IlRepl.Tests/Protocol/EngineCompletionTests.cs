using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Both engine transports complete read-only snapshots and remain usable after cancellation and mutation.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class EngineCompletionTests
{
    /// <summary>
    /// Supplies cancellation for engine requests and process startup.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// In-process and child-process engines return identical member edits and leave status unchanged.
    /// </summary>
    [TestMethod]
    public async Task Transports_AgreeWithoutSubmittingTheBuffer()
    {
        await using var local = new InProcessEngine();
        await using var remote = await HostPaths.StartEngineAsync(TestContext.CancellationToken);
        const string line = "call console::wr";
        var request = new CompletionRequest([line], 0, line.Length, null, []);
        var beforeLocal = local.Status;
        var beforeRemote = remote.Status;
        var left = await local.CompleteAsync(request, TestContext.CancellationToken);
        var right = await remote.CompleteAsync(request, TestContext.CancellationToken);
        Assert.AreSequenceEqual(left.Items, right.Items);
        Assert.AreEqual(left.ReplaceStart, right.ReplaceStart);
        Assert.AreEqual(left.ReplaceLength, right.ReplaceLength);
        Assert.AreEqual(beforeLocal, local.Status);
        Assert.AreEqual(beforeRemote, remote.Status);
        var after = await remote.HandleAsync("nop", TestContext.CancellationToken);
        Assert.IsTrue(after.Succeeded);
        Assert.HasCount(2, after.Lines);
    }

    /// <summary>
    /// Paging makes every matching overload reachable and rejects a cursor after a real mutation.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Paging_RejectsMutationAndPreservesOrder(bool useHost)
    {
        await using var engine = useHost
            ? (IReplEngine)await HostPaths.StartEngineAsync(TestContext.CancellationToken) : new InProcessEngine();
        const string line = "call string::";
        var request = new CompletionRequest([line], 0, line.Length, null, [], true);
        var first = await engine.CompleteAsync(request, TestContext.CancellationToken);
        Assert.HasCount(CompletionReply.PageSize, first.Items);
        Assert.IsNotNull(first.Cursor);
        var second = await engine.CompleteAsync(request with { Cursor = first.Cursor }, TestContext.CancellationToken);
        Assert.AreEqual(first.QueryId, second.QueryId);
        Assert.AreEqual(first.BindingEpoch, second.BindingEpoch);
        Assert.IsEmpty(first.Items.Select(item => item.InsertText).Intersect(second.Items.Select(item => item.InsertText)));
        var mark = engine.Status.Mark;
        await engine.HandleAsync("nop", TestContext.CancellationToken);
        await engine.RollbackAsync(mark, TestContext.CancellationToken);
        var stale = await engine.CompleteAsync(request with { Cursor = second.Cursor ?? first.Cursor }, TestContext.CancellationToken);
        Assert.AreEqual(-1, stale.Total);
        Assert.IsEmpty(stale.Items);
    }

    /// <summary>
    /// Cancelling completion leaves both the connection and the session ready for the next line.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancelledCompletion_LeavesTheEngineAnswering(bool useHost)
    {
        await using var engine = useHost
            ? (IReplEngine)await HostPaths.StartEngineAsync(TestContext.CancellationToken) : new InProcessEngine();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        await cancelled.CancelAsync();
        var request = new CompletionRequest(["ldtoken "], 0, 8, null, [], true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.CompleteAsync(request, cancelled.Token));
        var reply = await engine.HandleAsync("ldc.i4 42", TestContext.CancellationToken);
        Assert.IsTrue(reply.Succeeded);
        Assert.AreEqual("[int32]", reply.Status.Stack);
    }

    /// <summary>
    /// Disposal cancels active read-only work and waits until its metadata lease is released.
    /// </summary>
    [TestMethod]
    public async Task Dispose_SettlesCompletion()
    {
        var engine = new InProcessEngine();
        var pending = engine.CompleteAsync(new CompletionRequest(["ldtoken "], 0, 8, null, [], true), TestContext.CancellationToken);
        await engine.DisposeAsync();
        Assert.IsTrue(pending.IsCompleted);
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }
}
