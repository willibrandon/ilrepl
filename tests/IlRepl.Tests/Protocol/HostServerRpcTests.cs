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
}
