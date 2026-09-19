using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using IlRepl.Protocol;
using StreamJsonRpc;

namespace IlRepl.Processes;

/// <summary>
/// Retains the supervisor control channel and inherited diagnostic readers independently of process exit.
/// </summary>
internal sealed class SupervisorConnection : IAsyncDisposable
{
    private readonly LocalSocketListener _listener;
    private readonly Stream _stream;
    private DiagnosticTail _diagnostics = new();
    private readonly Task _drained;

    private SupervisorConnection(Process process, LocalSocketListener listener, Stream stream, JsonRpc rpc, long epoch)
    {
        Process = process;
        _listener = listener;
        _stream = stream;
        Rpc = rpc;
        Epoch = epoch;
        Service = rpc.Attach<ISupervisorService>();
        _drained = Task.WhenAll(DiagnosticTail.DrainAsync(process.StandardError, () => Volatile.Read(ref _diagnostics)),
            DiagnosticTail.DrainAsync(process.StandardOutput, () => Volatile.Read(ref _diagnostics)));
        rpc.StartListening();
    }

    /// <summary>
    /// The supervisor process, whose inherited diagnostic pipes may outlive it.
    /// </summary>
    internal Process Process { get; }

    /// <summary>
    /// The dedicated lifetime connection.
    /// </summary>
    internal JsonRpc Rpc { get; }

    /// <summary>
    /// The lifetime command proxy.
    /// </summary>
    internal ISupervisorService Service { get; }

    /// <summary>
    /// The generation accepted by this connection.
    /// </summary>
    internal long Epoch { get; }

    /// <summary>
    /// Starts a new diagnostic buffer before launching a replacement host.
    /// </summary>
    /// <returns>The buffer retained by that host's engine adapter.</returns>
    internal DiagnosticTail BeginDiagnostics()
    {
        var buffer = new DiagnosticTail();
        Interlocked.Exchange(ref _diagnostics, buffer);
        return buffer;
    }

    /// <summary>
    /// Starts the existing frontend executable in its private supervisor mode.
    /// </summary>
    /// <param name="epoch">The fresh supervisor generation.</param>
    /// <param name="assemblyPath">An isolated frontend package, or null for the current executable.</param>
    /// <param name="cancellationToken">Bounds startup.</param>
    /// <returns>A directly connected supervisor.</returns>
    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty assembly location selects the Native AOT executable path.")]
    internal static async Task<SupervisorConnection> StartAsync(long epoch, string? assemblyPath, CancellationToken cancellationToken)
    {
        var listener = new LocalSocketListener();
        var managedAssembly = assemblyPath ?? typeof(SupervisorConnection).Assembly.Location;
        var start = new ProcessStartInfo
        {
            FileName = managedAssembly.Length != 0 ? HostLocator.FindDotnet() : Environment.ProcessPath!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (managedAssembly.Length != 0)
        {
            start.ArgumentList.Add(managedAssembly);
        }

        start.ArgumentList.Add("--lifetime-supervisor");
        start.ArgumentList.Add(listener.SocketPath);
        start.ArgumentList.Add(epoch.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        using var frontend = Process.GetCurrentProcess();
        start.ArgumentList.Add(OwnedProcessGroup.GetStartIdentity(frontend).ToString(CultureInfo.InvariantCulture));
        start.Environment["ILREPL_SUPERVISOR_HANDSHAKE"] = listener.Secret;
        Process? process = null;
        try
        {
            process = Process.Start(start) ?? throw new IOException("the lifetime supervisor did not start");
            process.StandardInput.Close();
            var stream = await listener.AcceptAsync(process.Id, cancellationToken).ConfigureAwait(false);
            var rpc = new JsonRpc(RpcTransport.CreateHandler(stream, stream));
            return new SupervisorConnection(process, listener, stream, rpc, epoch);
        }
        catch
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }

                process.Dispose();
            }

            listener.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Confirms inherited diagnostics reached the frontend even when the original supervisor has exited.
    /// </summary>
    /// <param name="cancellationToken">Bounds the final drain.</param>
    /// <returns>Completion after the pipe boundary or final EOF.</returns>
    internal async Task DrainDiagnosticsAsync(CancellationToken cancellationToken)
    {
        bool exited;
        try
        {
            exited = Rpc.Completion.IsCompleted || Process.HasExited;
        }
        catch (InvalidOperationException)
        {
            exited = true;
        }

        if (exited)
        {
            await _drained.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var marker = "\u001eilrepl-diagnostics-" + Guid.NewGuid().ToString("N") + "\u001f";
        var acknowledged = _diagnostics.Mark(marker);
        await Service.FlushDiagnosticsAsync(marker, Epoch, cancellationToken).ConfigureAwait(false);
        await acknowledged.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            try
            {
                Rpc.Dispose();
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _listener.Dispose();
            }
        }
        finally
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Process.Kill();
                    await Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                Process.Dispose();
            }
        }
    }
}
