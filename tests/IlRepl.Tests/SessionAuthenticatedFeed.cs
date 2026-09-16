using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IlRepl.Tests;

/// <summary>
/// Serves one real local NuGet v3 package over HTTP with a Basic authentication challenge.
/// </summary>
internal sealed class SessionAuthenticatedFeed : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, byte[]> _resources;
    private readonly Task _server;
    private int _authorized;
    private int _unauthorized;

    /// <summary>
    /// Starts an isolated loopback source with no external network or process-wide configuration.
    /// </summary>
    /// <param name="packagePath">The actual package archive to serve.</param>
    /// <param name="id">The package ID.</param>
    /// <param name="version">The sole available version.</param>
    public SessionAuthenticatedFeed(string packagePath, string id, string version)
    {
        _listener.Start();
        var root = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
        Source = root + "/v3/index.json";
        var package = id.ToLowerInvariant();
        _resources = new(StringComparer.Ordinal)
        {
            ["/v3/index.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = "3.0.0", resources = new[]
                {
                    new Dictionary<string, string> { ["@id"] = root + "/flat/", ["@type"] = "PackageBaseAddress/3.0.0" },
                },
            }),
            ["/flat/" + package + "/index.json"] = JsonSerializer.SerializeToUtf8Bytes(new { versions = new[] { version } }),
            ["/flat/" + package + "/" + version + "/" + package + "." + version + ".nupkg"] = File.ReadAllBytes(packagePath),
        };
        _server = ServeAsync();
    }

    /// <summary>
    /// The private service index URL added to this test's NuGet.Config.
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// The username expected by the local server.
    /// </summary>
    public string Username { get; } = "session-fixture";

    /// <summary>
    /// A per-instance credential used only by the isolated fixture.
    /// </summary>
    public string Password { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The number of requests accepted with the configured credentials.
    /// </summary>
    public int AuthorizedRequests => Volatile.Read(ref _authorized);

    /// <summary>
    /// The number of authentication challenges sent before credentials were supplied.
    /// </summary>
    public int UnauthorizedRequests => Volatile.Read(ref _unauthorized);

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(_stop.Token);
                if (request is null)
                {
                    continue;
                }

                string? authorization = null;
                for (var index = 0; index < 100; index++)
                {
                    var header = await reader.ReadLineAsync(_stop.Token);
                    if (string.IsNullOrEmpty(header))
                    {
                        break;
                    }

                    if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                    {
                        authorization = header["Authorization:".Length..].Trim();
                    }
                }

                var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Username + ":" + Password));
                var authenticated = authorization == expected;
                if (authenticated) Interlocked.Increment(ref _authorized);
                else Interlocked.Increment(ref _unauthorized);
                var target = request.Split(' ')[1];
                var found = _resources.TryGetValue(target, out var resource);
                var status = !authenticated ? "401 Unauthorized" : found ? "200 OK" : "404 Not Found";
                var body = authenticated && found ? resource! : [];
                var headerText = "HTTP/1.1 " + status + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n"
                    + (!authenticated ? "WWW-Authenticate: Basic realm=\"session-fixture\"\r\n" : "")
                    + "Content-Type: " + (target.EndsWith(".nupkg", StringComparison.Ordinal)
                        ? "application/octet-stream" : "application/json") + "\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headerText), _stop.Token);
                if (!request.StartsWith("HEAD ", StringComparison.Ordinal))
                {
                    await stream.WriteAsync(body, _stop.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_stop.IsCancellationRequested)
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _server;
        _stop.Dispose();
    }
}
