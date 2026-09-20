using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace IlRepl.Protocol;

/// <summary>
/// Owns a private cross-platform Unix domain socket and authenticates one expected child connection.
/// </summary>
public sealed class LocalSocketListener : IDisposable
{
    private readonly Socket _listener;
    private readonly string _directory;
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly Guid _generation = Guid.NewGuid();
    private const int ProtocolVersion = 5;
    private int _disposed;

    /// <summary>
    /// Creates a listening endpoint before the execution host starts.
    /// </summary>
    public LocalSocketListener()
    {
        _directory = SocketDirectory.Create();
        SocketPath = Path.Join(_directory, "host.sock");
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            if (OperatingSystem.IsWindows())
            {
                SocketDirectory.SecureWindowsSocket(SocketPath);
            }

            _listener.Listen(4);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// The unique filesystem endpoint passed to the expected child.
    /// </summary>
    public string SocketPath { get; }

    /// <summary>
    /// The launch-specific secret passed only through the child's environment.
    /// </summary>
    public string Secret => Convert.ToHexString(_secret) + "." + _generation.ToString("N");

    /// <summary>
    /// Accepts the expected process and acknowledges rejection of stale or unrelated bootstrap connections.
    /// </summary>
    /// <param name="processId">The child process that owns this launch.</param>
    /// <param name="cancellationToken">Cancels acceptance and incomplete bootstrap reads.</param>
    /// <returns>The direct duplex stream owned by its caller.</returns>
    public async Task<NetworkStream> AcceptAsync(int processId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var socket = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                var bootstrap = new byte[60];
                await stream.ReadExactlyAsync(bootstrap, timeout.Token).ConfigureAwait(false);
                if (bootstrap.AsSpan(0, 4).SequenceEqual("ILRP"u8)
                    && BinaryPrimitives.ReadInt32LittleEndian(bootstrap.AsSpan(4)) == ProtocolVersion
                    && CryptographicOperations.FixedTimeEquals(_secret, bootstrap.AsSpan(8, 32))
                    && new Guid(bootstrap.AsSpan(40, 16)) == _generation
                    && BinaryPrimitives.ReadInt32LittleEndian(bootstrap.AsSpan(56)) == processId)
                {
                    await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                    return stream;
                }

                // Windows Unix sockets can leave a peer's pending read waiting after shutdown and close.
                // Send an explicit rejection so the peer never relies on EOF to recognize a refused bootstrap.
                await stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Connects a child directly to its frontend and waits for bootstrap acknowledgement.
    /// </summary>
    /// <param name="path">The frontend-owned socket endpoint.</param>
    /// <param name="secret">The launch-specific bootstrap secret.</param>
    /// <param name="cancellationToken">Cancels connection and acknowledgement.</param>
    /// <returns>The connected stream owned by the host.</returns>
    public static async Task<NetworkStream> ConnectAsync(string path, string secret, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        NetworkStream? stream = null;
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);
            var bootstrap = new byte[60];
            var parts = secret.Split('.');
            if (parts.Length != 2 || !Guid.TryParseExact(parts[1], "N", out var generation))
            {
                throw new ArgumentException("invalid host bootstrap identity", nameof(secret));
            }

            var credential = Convert.FromHexString(parts[0]);
            if (credential.Length != 32)
            {
                throw new ArgumentException("invalid host bootstrap secret", nameof(secret));
            }

            "ILRP"u8.CopyTo(bootstrap);
            BinaryPrimitives.WriteInt32LittleEndian(bootstrap.AsSpan(4), ProtocolVersion);
            credential.CopyTo(bootstrap, 8);
            generation.TryWriteBytes(bootstrap.AsSpan(40, 16));
            BinaryPrimitives.WriteInt32LittleEndian(bootstrap.AsSpan(56), Environment.ProcessId);
            await stream.WriteAsync(bootstrap, cancellationToken).ConfigureAwait(false);
            var acknowledgement = new byte[1];
            await stream.ReadExactlyAsync(acknowledgement, cancellationToken).ConfigureAwait(false);
            if (acknowledgement[0] != 1)
            {
                throw new IOException("the frontend rejected the host connection");
            }

            return stream;
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                socket.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Closes the listener and removes only this instance's endpoint directory.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _listener.Dispose();
        try
        {
            File.Delete(SocketPath);
            Directory.Delete(_directory);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (FileNotFoundException)
        {
        }
    }
}
