using Hex1b;
using Hex1b.Reflow;
using Hex1b.Sixel;

namespace IlRepl.Tui;

/// <summary>
/// Bounds optional capability discovery and settles console readers before restoring native terminal mode.
/// </summary>
internal sealed class ConsolePresentation : IHex1bTerminalPresentationAdapter, ITerminalReflowProvider
{
    private readonly ConsolePresentationAdapter _inner =
        new ConsolePresentationAdapter(enableMouse: true).WithSixelSupport(SixelPresentationSupport.None);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _reader = new(1, 1);
    private readonly List<Task<ReadOnlyMemory<byte>>> _reads = [];
    private CancellationTokenSource? _rawMode;
    private Task? _disposal;
    private bool _disposed;

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
            if (_rawMode is null || _disposed || ct.IsCancellationRequested) return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
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
                try { return await _inner.ReadInputAsync(cancellation.Token).ConfigureAwait(false); }
                finally { _reader.Release(); }
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
                if (_rawMode is not null) return;
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
        finally { _lifecycle.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask ExitRawModeAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try { await ExitCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
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
                try { await rawMode.CancelAsync().ConfigureAwait(false); }
                finally
                {
                    try { await Task.WhenAll(reads).ConfigureAwait(false); }
                    finally { rawMode.Dispose(); }
                }
            }
        }
        finally { await _inner.ExitRawModeAsync(CancellationToken.None).ConfigureAwait(false); }
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
            try { await ExitCoreAsync().ConfigureAwait(false); }
            finally { await _inner.DisposeAsync().ConfigureAwait(false); }
        }
        finally { _lifecycle.Release(); }
    }
}
