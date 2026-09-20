using Hex1b;
using Hex1b.Reflow;
using Hex1b.Sixel;

namespace IlRepl.Tui;

/// <summary>
/// Bounds optional capability discovery and settles console readers before restoring native terminal mode.
/// </summary>
internal sealed class ConsolePresentation : IHex1bTerminalPresentationAdapter, ITerminalReflowProvider
{
    private static readonly byte[] s_releaseScreen = "\u001b[?2026l"u8.ToArray();
    private readonly ConsolePresentationAdapter _inner =
        new ConsolePresentationAdapter(enableMouse: true).WithSixelSupport(SixelPresentationSupport.None);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _reader = new(1, 1);
    private readonly List<Task<ReadOnlyMemory<byte>>> _reads = [];
    private readonly TerminalReplyBuffer _input = new();
    private CancellationTokenSource? _rawMode;
    private Task? _disposal;
    private bool _disposed;

    /// <summary>
    /// Observes actual native read boundaries before terminal replies are framed.
    /// </summary>
    internal Action<ReadOnlyMemory<byte>>? InputObserved { get; set; }

    /// <inheritdoc />
    public int Width => _inner.Width;

    /// <inheritdoc />
    public int Height => _inner.Height;

    /// <inheritdoc />
    public TerminalCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public bool AnswersProtocolQueriesDirectly => _inner.AnswersProtocolQueriesDirectly;

    /// <inheritdoc />
    public bool ReflowEnabled => _inner.ReflowEnabled;

    /// <inheritdoc />
    public bool ShouldClearSoftWrapOnAbsolutePosition => _inner.ShouldClearSoftWrapOnAbsolutePosition;

    /// <inheritdoc />
    public event Action<int, int>? Resized
    {
        add => _inner.Resized += value;
        remove => _inner.Resized -= value;
    }

    /// <inheritdoc />
    public event Action? Disconnected
    {
        add => _inner.Disconnected += value;
        remove => _inner.Disconnected -= value;
    }

    /// <inheritdoc />
    public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => _inner.WriteOutputAsync(data, ct);

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_rawMode is null || _disposed || ct.IsCancellationRequested)
            {
                return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _rawMode.Token);
            _reads.RemoveAll(static read => read.IsCompleted);
            var read = ReadCoreAsync(cancellation);
            _reads.Add(read);
            return new ValueTask<ReadOnlyMemory<byte>>(read);
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadCoreAsync(CancellationTokenSource cancellation)
    {
        using (cancellation)
        {
            try
            {
                await _reader.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    while (true)
                    {
                        var buffered = _input.ReadBuffered();
                        if (!buffered.IsEmpty)
                        {
                            return buffered;
                        }

                        using var escape = _input.NeedsEscapeTimeout
                            ? CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token) : null;
                        escape?.CancelAfter(TimeSpan.FromMilliseconds(50));
                        ReadOnlyMemory<byte> bytes;
                        try
                        {
                            bytes = await _inner.ReadInputAsync(escape?.Token ?? cancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (escape is { IsCancellationRequested: true }
                            && !cancellation.IsCancellationRequested)
                        {
                            return _input.FlushEscape();
                        }

                        if (bytes.IsEmpty)
                        {
                            // The native driver polls at most every 100 ms. Join that read before returning Escape,
                            // encoded unambiguously so Hex1b does not add its own second 50 ms timeout.
                            return escape is { IsCancellationRequested: true } && !cancellation.IsCancellationRequested
                                ? _input.FlushEscape() : ReadOnlyMemory<byte>.Empty;
                        }

                        InputObserved?.Invoke(bytes);
                        var framed = _input.Append(bytes);
                        if (!framed.IsEmpty)
                        {
                            return framed;
                        }
                    }
                }
                finally
                {
                    _reader.Release();
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return ReadOnlyMemory<byte>.Empty;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct = default) => _inner.FlushAsync(ct);

    /// <inheritdoc />
    public async ValueTask EnterRawModeAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_rawMode is not null)
                {
                    return;
                }
            }

            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromMilliseconds(25));
            try
            {
                // The capability probe settles its own reader and retains prefetched input before ordinary reads are admitted.
                await _inner.EnterRawModeAsync(probe.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (probe.IsCancellationRequested && !ct.IsCancellationRequested)
            {
            }

            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _rawMode = new CancellationTokenSource();
            }
        }
        catch
        {
            await ExitCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask ExitRawModeAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExitCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task ExitCoreAsync()
    {
        CancellationTokenSource? rawMode;
        Task<ReadOnlyMemory<byte>>[] reads;
        lock (_sync)
        {
            rawMode = _rawMode;
            _rawMode = null;
            reads = _reads.ToArray();
            _reads.Clear();
        }

        try
        {
            if (rawMode is not null)
            {
                try
                {
                    await rawMode.CancelAsync().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await Task.WhenAll(reads).ConfigureAwait(false);
                    }
                    finally
                    {
                        rawMode.Dispose();
                    }
                }
            }
        }
        finally
        {
            _input.Reset();
            if (rawMode is not null)
            {
                await ReleaseScreenAsync().ConfigureAwait(false);
            }

            await _inner.ExitRawModeAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Every frame opens with "hold the screen" and closes with "release it". The frame that a quit cuts short never sends its
    // release, and a terminal such as Ghostty then keeps showing the old screen until its own timeout, about a second after
    // the program has gone. Sending the release on the way out costs nothing on a terminal that does not know the mode.
    private async Task ReleaseScreenAsync()
    {
        try
        {
            await _inner.WriteOutputAsync(s_releaseScreen, CancellationToken.None).ConfigureAwait(false);
            await _inner.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The terminal is already gone, so there is no screen left to release.
        }
    }

    /// <inheritdoc />
    public (int Row, int Column) GetCursorPosition() => _inner.GetCursorPosition();

    /// <inheritdoc />
    public ReflowResult Reflow(ReflowContext context) => _inner.Reflow(context);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposed = true;
            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Publish the shared disposal task before any inner callback can observe this adapter again.
        await Task.Yield();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await ExitCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }
}
