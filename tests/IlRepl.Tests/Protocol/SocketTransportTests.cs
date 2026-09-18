using System.Buffers.Binary;
using System.Net.Sockets;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Exercises the real cross-platform socket transport and isolated endpoint lifetime.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class SocketTransportTests
{
    /// <summary>
    /// Supplies cancellation to actual socket operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Independent authenticated connections exchange exact duplex bytes and remove their own endpoints.
    /// </summary>
    [TestMethod]
    public async Task Connections_ExchangeBytesAndCleanUpIndependentPaths()
    {
        using var first = new LocalSocketListener();
        using var second = new LocalSocketListener();
        Assert.AreNotEqual(first.SocketPath, second.SocketPath);
        var token = TestContext.CancellationToken;
        var accepting = first.AcceptAsync(Environment.ProcessId, token);
        await using var client = await LocalSocketListener.ConnectAsync(first.SocketPath, first.Secret, token);
        await using var server = await accepting;
        await client.WriteAsync("from host"u8.ToArray(), token);
        var received = new byte[9];
        await server.ReadExactlyAsync(received, token);
        Assert.AreSequenceEqual("from host"u8.ToArray(), received);
        await server.WriteAsync("from frontend"u8.ToArray(), token);
        received = new byte[13];
        await client.ReadExactlyAsync(received, token);
        Assert.AreSequenceEqual("from frontend"u8.ToArray(), received);
        first.Dispose();
        Assert.IsFalse(File.Exists(first.SocketPath));
        Assert.IsTrue(File.Exists(second.SocketPath));
    }

    /// <summary>
    /// Cancelling a pending accept leaves the endpoint usable for a later authenticated connection.
    /// </summary>
    [TestMethod]
    public async Task CancelledAccept_DoesNotPoisonTheListener()
    {
        using var listener = new LocalSocketListener();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var cancelled = listener.AcceptAsync(Environment.ProcessId, pending.Token);
        await pending.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        var accepting = listener.AcceptAsync(Environment.ProcessId, TestContext.CancellationToken);
        await using var client = await LocalSocketListener.ConnectAsync(listener.SocketPath, listener.Secret,
            TestContext.CancellationToken);
        await using var server = await accepting;
        Assert.IsTrue(server.CanRead);
        Assert.IsTrue(client.CanWrite);
    }
    /// <summary>
    /// A stale generation, wrong version, or unrelated process cannot consume the expected host connection.
    /// </summary>
    /// <param name="mismatch">The invalid bootstrap field.</param>
    [TestMethod]
    [DataRow("version")]
    [DataRow("generation")]
    [DataRow("process")]
    public async Task InvalidBootstrap_IsRejectedBeforeTheRealHostConnects(string mismatch)
    {
        using var listener = new LocalSocketListener();
        var token = TestContext.CancellationToken;
        var accepting = listener.AcceptAsync(Environment.ProcessId, token);
        using (var impostor = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await impostor.ConnectAsync(new UnixDomainSocketEndPoint(listener.SocketPath), token);
            await using var stream = new NetworkStream(impostor);
            var bootstrap = new byte[60];
            "ILRP"u8.CopyTo(bootstrap);
            BinaryPrimitives.WriteInt32LittleEndian(bootstrap.AsSpan(4), mismatch == "version" ? 999 : 1);
            var credentials = listener.Secret.Split('.');
            Convert.FromHexString(credentials[0]).CopyTo(bootstrap, 8);
            var generation = mismatch == "generation" ? Guid.NewGuid() : Guid.ParseExact(credentials[1], "N");
            generation.TryWriteBytes(bootstrap.AsSpan(40, 16));
            BinaryPrimitives.WriteInt32LittleEndian(bootstrap.AsSpan(56),
                mismatch == "process" ? Environment.ProcessId + 1 : Environment.ProcessId);
            await stream.WriteAsync(bootstrap, token);
            var acknowledgement = new byte[1];
            Assert.AreEqual(0, await stream.ReadAsync(acknowledgement, token));
        }
        Assert.IsFalse(accepting.IsCompleted);
        await using var real = await LocalSocketListener.ConnectAsync(listener.SocketPath, listener.Secret, token);
        await using var accepted = await accepting;
        await real.WriteAsync(new byte[] { 42 }, token);
        var result = new byte[1];
        await accepted.ReadExactlyAsync(result, token);
        Assert.AreEqual((byte)42, result[0]);
    }
}
