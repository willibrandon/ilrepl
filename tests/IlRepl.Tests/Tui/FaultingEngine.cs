using IlRepl.Protocol;

namespace IlRepl.Tests.Tui;

/// <summary>
/// An engine whose transport fails on one chosen line, the way a host that has died fails: the
/// call throws instead of answering, and every later call answers again.
/// </summary>
internal sealed class FaultingEngine : IReplEngine
{
    private readonly IReplEngine _inner;
    private readonly int _failAt;
    private readonly Func<Exception> _fault;
    private int _calls;

    /// <summary>
    /// Initializes a fault in front of an engine.
    /// </summary>
    /// <param name="inner">The engine that answers.</param>
    /// <param name="failAt">The zero-based call that throws.</param>
    /// <param name="fault">What it throws; a transport failure unless given.</param>
    public FaultingEngine(IReplEngine inner, int failAt, Func<Exception>? fault = null)
    {
        _inner = inner;
        _failAt = failAt;
        _fault = fault ?? (() => new ReplEngineException("the host has exited"));
    }

    /// <summary>
    /// How many lines have been asked for.
    /// </summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> Catalog => _inner.Catalog;

    /// <inheritdoc />
    public CilVocabulary Vocabulary => _inner.Vocabulary;

    /// <inheritdoc />
    public SessionStatus Status => _inner.Status;

    /// <inheritdoc />
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls) - 1;
        return call == _failAt
            ? throw _fault()
            : _inner.HandleAsync(line, cancellationToken);
    }

    /// <inheritdoc />
    public Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken) => _inner.RollbackAsync(mark, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
