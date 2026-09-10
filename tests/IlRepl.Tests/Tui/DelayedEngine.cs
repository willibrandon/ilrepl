using System.Threading.Channels;
using IlRepl.Protocol;

namespace IlRepl.Tests.Tui;

/// <summary>
/// An engine that answers only when the test lets it: each line waits for one permit before it
/// reaches the real engine, so a test can hold a submission in flight, watch the screen, press
/// keys, and then let the lines through one at a time.
/// </summary>
internal sealed class DelayedEngine : IReplEngine
{
    private readonly IReplEngine _inner;
    private readonly Channel<bool> _permits = Channel.CreateUnbounded<bool>();
    private readonly List<string> _handled = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a gate in front of an engine.
    /// </summary>
    /// <param name="inner">The engine that answers.</param>
    public DelayedEngine(IReplEngine inner)
    {
        _inner = inner;
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
    public SessionStatus Status => _inner.Status;

    /// <inheritdoc/>
    public long AssemblyVersion => _inner.AssemblyVersion;

    /// <inheritdoc/>
    public Task<long> WaitForAssembliesAsync(long version, CancellationToken cancellationToken) =>
        _inner.WaitForAssembliesAsync(version, cancellationToken);


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
        lock (_lock)
        {
            _handled.Add(line);
        }

        return reply;
    }

    /// <inheritdoc />
    public Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken) => _inner.RollbackAsync(mark, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
