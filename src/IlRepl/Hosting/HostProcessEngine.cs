using System.Diagnostics;
using System.Text;
using IlRepl.Protocol;
using StreamJsonRpc;

namespace IlRepl.Hosting;

/// <summary>
/// Runs the framework-dependent host as a child process and talks to it over JSON-RPC on its
/// standard streams.
/// </summary>
public sealed class HostProcessEngine : IReplEngine
{
    private readonly Process _process;
    private readonly JsonRpc _rpc;
    private readonly IReplHost _host;
    private readonly StringBuilder _stderr;
    private bool _disposed;

    private HostProcessEngine(Process process, JsonRpc rpc, IReplHost host, StringBuilder stderr, HostHello hello)
    {
        _process = process;
        _rpc = rpc;
        _host = host;
        _stderr = stderr;
        Catalog = hello.Catalog;
        Vocabulary = hello.Vocabulary;
        Status = hello.Status;
    }

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> Catalog { get; }

    /// <inheritdoc />
    public CilVocabulary Vocabulary { get; }

    /// <inheritdoc />
    public SessionStatus Status { get; private set; }

    /// <summary>
    /// Starts the host and waits for its hello.
    /// </summary>
    /// <param name="hostAssemblyPath">The host assembly, or null to locate it with <see cref="HostLocator"/>.</param>
    /// <param name="workingDirectory">The working directory for the host, or null for the current one.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running engine.</returns>
    /// <exception cref="HostProtocolException">The host could not be started or did not answer.</exception>
    public static async Task<HostProcessEngine> StartAsync(string? hostAssemblyPath = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var hostPath = hostAssemblyPath ?? HostLocator.FindHost();
        var startInfo = new ProcessStartInfo
        {
            FileName = HostLocator.FindDotnet(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add(hostPath);
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new HostProtocolException("the host process did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new HostProtocolException($"could not start '{startInfo.FileName} {hostPath}': {ex.Message}", ex);
        }

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };
        process.BeginErrorReadLine();

        JsonRpc? rpc = null;
        try
        {
            rpc = new JsonRpc(RpcTransport.CreateHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream));
            var host = rpc.Attach<IReplHost>();
            rpc.StartListening();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var hello = await host.HelloAsync(timeout.Token).ConfigureAwait(false);
            return new HostProcessEngine(process, rpc, host, stderr, hello);
        }
        catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException or OperationCanceledException or IOException)
        {
            string detail;
            lock (stderr)
            {
                detail = Tail(stderr.ToString());
            }

            rpc?.Dispose();
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.Dispose();
            throw new HostProtocolException("the host did not answer: " + ex.Message + (detail.Length == 0 ? "" : "\n" + detail), ex);
        }
    }

    /// <inheritdoc />
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        return CallAsync(() => _host.HandleAsync(line, cancellationToken));
    }

    /// <inheritdoc />
    public Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return CallAsync(() => _host.RollbackAsync(mark, cancellationToken));
    }

    private async Task<HandleReply> CallAsync(Func<Task<HandleReply>> call)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var reply = await call().ConfigureAwait(false);
            Status = reply.Status;
            return reply;
        }
        catch (RemoteInvocationException ex)
        {
            throw new HostProtocolException("the host failed: " + ex.Message, ex);
        }
        catch (ConnectionLostException ex)
        {
            throw new HostProtocolException("the host exited" + ExitDetail(), ex);
        }
    }

    /// <summary>
    /// Closes the connection and waits briefly for the host to exit.
    /// </summary>
    /// <returns>A task that completes when the host is gone.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rpc.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The host is already gone.
        }
        finally
        {
            _process.Dispose();
        }
    }

    private string ExitDetail()
    {
        string tail;
        lock (_stderr)
        {
            tail = Tail(_stderr.ToString());
        }

        var code = _process.HasExited ? $" with code {_process.ExitCode}" : "";
        return code + (tail.Length == 0 ? "" : ":\n" + tail);
    }

    private static string Tail(string text)
    {
        var lines = text.TrimEnd().Split('\n');
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - 12))).Trim();
    }
}
