using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Repl;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Tests for <see cref="HostServer"/> over a real JSON-RPC connection in-process.
/// </summary>
[TestClass]
public sealed class HostServerRpcTests
{
    private static (JsonRpc Server, JsonRpc Client, IReplHost Proxy) Connect()
    {
        var (clientPipe, serverPipe) = FullDuplexStream.CreatePair();
        var server = new JsonRpc(RpcTransport.CreateHandler(serverPipe, serverPipe));
        server.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IReplHost>(), new HostServer(new ReplCore()), null);
        server.StartListening();
        var client = new JsonRpc(RpcTransport.CreateHandler(clientPipe, clientPipe));
        var proxy = client.Attach<IReplHost>();
        client.StartListening();
        return (server, client, proxy);
    }

    /// <summary>
    /// Hello carries the catalog and the initial status.
    /// </summary>
    [TestMethod]
    public async Task Hello_ReturnsCatalogAndStatus()
    {
        var (server, client, proxy) = Connect();
        using (server)
        using (client)
        {
            var hello = await proxy.HelloAsync(TestContext.CancellationToken);
            Assert.IsGreaterThan(220, hello.Catalog.Count);
            Assert.AreEqual("il[1]> ", hello.Status.Prompt);
            Assert.IsTrue(hello.Status.CellIsEmpty);
            Assert.IsNull(hello.Status.OpenMethod);
            Assert.AreEqual(0, hello.Status.Methods);
            Assert.Contains(i => i.Name == ".method", hello.Catalog);
        }
    }

    /// <summary>
    /// Handled lines come back with transcript lines, and styles survive serialization.
    /// </summary>
    [TestMethod]
    public async Task Handle_RoundTripsTranscriptLines()
    {
        var (server, client, proxy) = Connect();
        using (server)
        using (client)
        {
            var first = await proxy.HandleAsync("ldc.i4 6", TestContext.CancellationToken);
            Assert.IsTrue(first.Succeeded);
            Assert.AreEqual("[int32]", first.Status.Stack);
            Assert.Contains(l => l.Kind == LineKind.Stack && l.Spans.Any(s => s.Style == SpanStyle.TopType && s.Text == "int32"), first.Lines);

            await proxy.HandleAsync("ldc.i4 7", TestContext.CancellationToken);
            await proxy.HandleAsync("mul", TestContext.CancellationToken);
            var result = await proxy.HandleAsync("ret", TestContext.CancellationToken);
            Assert.Contains(l => l.Kind == LineKind.Result && l.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
            Assert.AreEqual(2, result.Status.CellNumber);
        }
    }

    /// <summary>
    /// The transcript buffer is drained per reply, so lines are not repeated.
    /// </summary>
    [TestMethod]
    public async Task Handle_DoesNotRepeatLines()
    {
        var (server, client, proxy) = Connect();
        using (server)
        using (client)
        {
            var first = await proxy.HandleAsync("ldc.i4 1", TestContext.CancellationToken);
            var second = await proxy.HandleAsync("ldc.i4 2", TestContext.CancellationToken);
            Assert.HasCount(2, first.Lines);
            Assert.HasCount(2, second.Lines);
            Assert.AreEqual("ldc.i4 2", second.Lines[0].Spans[^1].Text);
        }
    }

    /// <summary>
    /// The test context, for cancellation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The status reports the open method while a block is typed and the count once it commits.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task Handle_ReportsOpenMethodAndMethodCount()
    {
        var (server, client, proxy) = Connect();
        using (server)
        using (client)
        {
            var ct = TestContext.CancellationToken;
            var open = await proxy.HandleAsync(".method int32 Two() {", ct);
            Assert.IsTrue(open.Succeeded);
            Assert.AreEqual("Two", open.Status.OpenMethod);
            Assert.AreEqual(1, open.Status.CellNumber);

            await proxy.HandleAsync("ldc.i4 2", ct);
            await proxy.HandleAsync("ret", ct);
            var close = await proxy.HandleAsync("}", ct);
            Assert.IsTrue(close.Succeeded);
            Assert.IsNull(close.Status.OpenMethod);
            Assert.AreEqual(1, close.Status.Methods);
            Assert.AreEqual(2, close.Status.CellNumber);
            Assert.Contains(l => l.PlainText == "  end of method Two", close.Lines);

            await proxy.HandleAsync("call int32 Two()", ct);
            var run = await proxy.HandleAsync("ret", ct);
            Assert.Contains(l => l.Kind == LineKind.Result && l.PlainText.Contains("= 2 : int32", StringComparison.Ordinal), run.Lines);
            Assert.AreEqual(3, run.Status.CellNumber);
        }
    }

    /// <summary>
    /// A rollback crosses the wire with its mark and comes back with the note and the status.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task Rollback_RoundTrips()
    {
        var (server, client, proxy) = Connect();
        using (server)
        using (client)
        {
            var hello = await proxy.HelloAsync(TestContext.CancellationToken);
            var mark = hello.Status.Mark;
            await proxy.HandleAsync(".method int32 F() {", TestContext.CancellationToken);
            var refused = await proxy.HandleAsync("lcd.i4 1", TestContext.CancellationToken);
            Assert.IsFalse(refused.Succeeded);
            Assert.AreEqual(mark.Generation, refused.Status.Mark.Generation);
            Assert.AreEqual(1, refused.Status.OpenDepth);

            var reply = await proxy.RollbackAsync(mark, TestContext.CancellationToken);
            Assert.IsTrue(reply.Succeeded);
            Assert.Contains(l => l.Kind == LineKind.Info && l.PlainText.Contains("method F abandoned", StringComparison.Ordinal), reply.Lines);
            Assert.IsNull(reply.Status.OpenMethod);
            Assert.AreEqual(0, reply.Status.OpenDepth);

            await proxy.HandleAsync("ldc.i4 1", TestContext.CancellationToken);
            var ran = await proxy.HandleAsync("ret", TestContext.CancellationToken);
            var stale = await proxy.RollbackAsync(mark, TestContext.CancellationToken);
            Assert.IsFalse(stale.Succeeded);
            Assert.AreNotEqual(mark.Generation, ran.Status.Mark.Generation);
        }
    }
}
