using System.Threading.Channels;
using IlRepl.Protocol;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Gates real engine invocation and completed replies independently to exercise cancellation and committed-result races.
/// </summary>
internal sealed class DelayedEngine : IReplEngine
{
    private readonly IReplEngine _inner;
    private readonly Channel<bool> _permits = Channel.CreateUnbounded<bool>();
    private readonly List<string> _handled = [];
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource _replyPermit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SessionStatus _status;
    private int _holdReplyAt;
    private int _replyWaiting;

    /// <summary>
    /// Initializes a gate in front of an engine.
    /// </summary>
    /// <param name="inner">The engine that answers.</param>
    public DelayedEngine(IReplEngine inner)
    {
        _inner = inner;
        _status = inner.Status;
    }

    /// <summary>
    /// The lines the real engine has answered, in order.
    /// </summary>
    public IReadOnlyList<string> Handled
    {
        get
        {
            lock (_lock)
            {
                return _handled.ToArray();
            }
        }
    }

    /// <summary>
    /// How many lines are waiting for a permit.
    /// </summary>
    public int Waiting => Volatile.Read(ref _waiting);

    private int _waiting;

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> Catalog => _inner.Catalog;

    /// <inheritdoc />
    public CilVocabulary Vocabulary => _inner.Vocabulary;

    /// <inheritdoc />
    public SessionStatus Status => Volatile.Read(ref _status);

    /// <inheritdoc/>
    public long AssemblyVersion => _inner.AssemblyVersion;

    /// <inheritdoc/>
    public Task<long> WaitForAssembliesAsync(long version, CancellationToken cancellationToken) =>
        _inner.WaitForAssembliesAsync(version, cancellationToken);

    /// <inheritdoc/>
    public Task<AnalysisReply> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken) =>
        _inner.AnalyzeAsync(request, cancellationToken);

    /// <inheritdoc/>
    public Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken) =>
        _inner.CompleteAsync(request, cancellationToken);

    /// <summary>
    /// Lets a number of lines through.
    /// </summary>
    /// <param name="count">How many.</param>
    public void Allow(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            _permits.Writer.TryWrite(true);
        }
    }

    /// <summary>
    /// Holds one genuine completed response until the test acknowledges its delivery.
    /// </summary>
    /// <param name="count">The one-based handled call whose response is held.</param>
    public void HoldReplyAt(int count) => Volatile.Write(ref _holdReplyAt, count);

    /// <summary>
    /// Whether a real completed engine response is waiting for frontend delivery.
    /// </summary>
    public bool ReplyWaiting => Volatile.Read(ref _replyWaiting) != 0;

    /// <summary>
    /// Delivers the held completed response even if cancellation arrived after its commit.
    /// </summary>
    public void ReleaseReply() => _replyPermit.TrySetResult();

    /// <inheritdoc />
    public async Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _permits.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        var reply = await _inner.HandleAsync(line, cancellationToken).ConfigureAwait(false);
        int count;
        lock (_lock)
        {
            _handled.Add(line);
            count = _handled.Count;
        }

        if (count == Volatile.Read(ref _holdReplyAt))
        {
            Volatile.Write(ref _replyWaiting, 1);
            try
            {
                await _replyPermit.Task.ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _replyWaiting, 0);
            }
        }

        Volatile.Write(ref _status, reply.Status);
        return reply;
    }

    /// <inheritdoc />
    public async Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken)
    {
        var reply = await _inner.RollbackAsync(mark, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _status, reply.Status);
        return reply;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        ReleaseReply();
        return _inner.DisposeAsync();
    }
}
