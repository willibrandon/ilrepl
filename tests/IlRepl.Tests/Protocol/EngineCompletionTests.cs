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

    /// <summary>
    /// Completion waits for an executing cell and observes its committed revision through both transports.
    /// </summary>
    /// <param name="useHost">Whether to use the real host process.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentHandle_CompletesBeforeTheSnapshot(bool useHost)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = useHost ? (IReplEngine)await HostPaths.StartEngineAsync(ct) : new InProcessEngine();
        var marker = Path.Combine(Path.GetTempPath(), "ilrepl-completion-gate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var escaped = marker.Replace("\\", "\\\\", StringComparison.Ordinal);
            foreach (var line in new[] { $"ldstr \"{escaped}\"", "ldstr \"started\"",
                "call File::WriteAllText(string, string)", "ldc.i4 500", "call Thread::Sleep(int32)" })
            {
                Assert.IsTrue((await engine.HandleAsync(line, ct)).Succeeded, line);
            }

            var running = Task.Run(() => engine.HandleAsync("ret", ct), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker))
            {
                await Task.Delay(5, timeout.Token);
            }

            const string prefix = "call Environment::get_CurrentManagedTh";
            var completing = engine.CompleteAsync(new CompletionRequest([prefix], 0, prefix.Length, null, []), ct);
            Assert.IsFalse(running.IsCompleted, "The marker is written before the half-second runtime pause.");
            var completed = await running;
            var snapshot = await completing;
            Assert.IsTrue(completed.Succeeded);
            Assert.AreEqual(completed.Status.Revision, snapshot.Revision);
            Assert.HasCount(1, snapshot.Items);
            Assert.AreEqual("Environment::get_CurrentManagedThreadId()", snapshot.Items[0].InsertText);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    /// <summary>
    /// A generic method's selected arity survives RPC while arguments and the final signature are completed.
    /// </summary>
    /// <param name="useHost">Whether to use the real host process.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Anchors_RetainTheirOwnerAcrossTransportRequests(bool useHost)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = useHost ? (IReplEngine)await HostPaths.StartEngineAsync(ct) : new InProcessEngine();
        const string prefix = "call Array::Empt";
        var first = await engine.CompleteAsync(new CompletionRequest([prefix], 0, prefix.Length, null, []), ct);
        var method = first.Items.Single();
        Assert.IsNotNull(method.Continuation);
        var open = "call " + method.InsertText;
        var anchor = new ContinuationAnchor(0, 5, open.Length, method.Continuation);
        var argumentLine = open + "str";
        var arguments = await engine.CompleteAsync(
            new CompletionRequest([argumentLine], 0, argumentLine.Length, null, [anchor]), ct);
        var argument = arguments.Items.Single(item => item.InsertText == "string");
        var closed = open + argument.InsertText + ">";
        var final = await engine.CompleteAsync(new CompletionRequest([closed], 0, closed.Length, null, [anchor]), ct);
        Assert.HasCount(1, final.Items);
        var accepted = closed[..final.ReplaceStart] + final.Items[0].InsertText
            + closed[(final.ReplaceStart + final.ReplaceLength)..];
        Assert.IsTrue((await engine.HandleAsync(accepted, ct)).Succeeded);
        Assert.IsTrue((await engine.HandleAsync("ldlen", ct)).Succeeded);
        Assert.IsTrue((await engine.HandleAsync("conv.i4", ct)).Succeeded);
        var run = await engine.HandleAsync("ret", ct);
        Assert.IsTrue(run.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("= 0 : int32", StringComparison.Ordinal), run.Lines);
    }

    /// <summary>
    /// Both transports complete known facades and refuse unknown references without runtime loads or resolution callbacks.
    /// </summary>
    /// <param name="useHost">Whether to use the real host process.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Snapshot_ResolvesFacadesWithoutRuntimeCallbacks(bool useHost)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = useHost ? (IReplEngine)await HostPaths.StartEngineAsync(ct) : new InProcessEngine();
        Assert.IsTrue((await engine.HandleAsync(".load " + SampleHost.Samples.GreeterDll, ct)).Succeeded);
        foreach (var line in new[] { "call [Greeter]Greeter.CompletionProbe::Begin()", "ret" })
        {
            Assert.IsTrue((await engine.HandleAsync(line, ct)).Succeeded);
        }

        var status = engine.Status;
        const string prefix = "ldtoken [System.Runtime]System.Strin";
        var request = new CompletionRequest([prefix], 0, prefix.Length, null, []);
        var known = await engine.CompleteAsync(request, ct);
        Assert.Contains(item => item.InsertText == "string", known.Items);
        const string missing = "call [NotLoaded]Missing::M";
        var unknown = await engine.CompleteAsync(new CompletionRequest([missing], 0, missing.Length, null, []), ct);
        Assert.IsEmpty(unknown.Items);
        Assert.AreEqual(status, engine.Status);
        foreach (var line in new[] { ".clear", "call [System.Runtime]System.Reflection.Assembly::GetExecutingAssembly()",
            "call [Greeter]Greeter.CompletionProbe::Report([System.Runtime]System.Reflection.Assembly)" })
        {
            Assert.IsTrue((await engine.HandleAsync(line, ct)).Succeeded);
        }

        var report = await engine.HandleAsync("ret", ct);
        Assert.IsTrue(report.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("preview loads=0; modules=0; callbacks=0;", StringComparison.Ordinal),
            report.Lines, string.Join("\n", report.Lines.Select(line => line.PlainText)));
    }
}
