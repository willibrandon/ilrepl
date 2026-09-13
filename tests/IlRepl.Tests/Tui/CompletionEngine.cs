using System.Collections.Concurrent;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Controls completion replies while retaining the real engine's vocabulary and mutation behavior.
/// </summary>
internal sealed class CompletionEngine : IReplEngine
{
    private readonly InProcessEngine _inner = new();
    private TaskCompletionSource<long> _assembliesChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Every completion call in arrival order.
    /// </summary>
    public List<HeldCompletion> Calls { get; } = [];

    /// <summary>
    /// An optional synchronous response used to test same-frame publication.
    /// </summary>
    public CompletionReply? Immediate { get; set; }

    /// <inheritdoc/>
    public IReadOnlyList<CompletionItem> Catalog => _inner.Catalog;

    /// <inheritdoc/>
    public CilVocabulary Vocabulary => _inner.Vocabulary;

    /// <inheritdoc/>
    public SessionStatus Status => _inner.Status;

    /// <inheritdoc/>
    public long AssemblyVersion { get; private set; }

    /// <inheritdoc/>
    public Task<long> WaitForAssembliesAsync(long version, CancellationToken cancellationToken) =>
        version == AssemblyVersion ? _assembliesChanged.Task.WaitAsync(cancellationToken) : Task.FromResult(AssemblyVersion);

    /// <summary>
    /// Reports an assembly load independently of the session revision and any pending replies.
    /// </summary>
    public void ChangeAssemblies()
    {
        var previous = _assembliesChanged;
        _assembliesChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult(++AssemblyVersion);
    }

    /// <summary>
    /// Whether analysis responses are controlled by the test.
    /// </summary>
    public bool HoldAnalysis { get; set; }

    /// <summary>
    /// The held requests in arrival order.
    /// </summary>
    public ConcurrentQueue<HeldAnalysis> Analyses { get; } = new();

    /// <inheritdoc/>
    public Task<AnalysisReply> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        if (!HoldAnalysis)
        {
            return _inner.AnalyzeAsync(request, cancellationToken);
        }

        var call = new HeldAnalysis(request, cancellationToken);
        Analyses.Enqueue(call);
        return call.Answer.Task;
    }

    /// <inheritdoc/>
    public Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        var call = new HeldCompletion(request, cancellationToken);
        Calls.Add(call);
        return Immediate is { } reply ? Task.FromResult(reply) : call.Answer.Task;
    }

    /// <inheritdoc/>
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken) => _inner.HandleAsync(line, cancellationToken);

    /// <inheritdoc/>
    public Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken) =>
        _inner.RollbackAsync(mark, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var call in Calls)
        {
            call.Answer.TrySetCanceled();
        }

        foreach (var call in Analyses)
        {
            call.Answer.TrySetCanceled();
        }

        await _inner.DisposeAsync();
    }
}
