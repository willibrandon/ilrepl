namespace IlRepl.Protocol;

/// <summary>
/// Supplies editing vocabulary while an execution host is starting or unavailable.
/// </summary>
internal sealed class InactiveEngine : IReplEngine
{
    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> Catalog => BootstrapCatalog.Hello.Catalog;

    /// <inheritdoc />
    public CilVocabulary Vocabulary => BootstrapCatalog.Hello.Vocabulary;

    /// <inheritdoc />
    public SessionStatus Status => BootstrapCatalog.Hello.Status;

    /// <inheritdoc />
    public long AssemblyVersion => 0;

    /// <inheritdoc />
    public async Task<long> WaitForAssembliesAsync(long version, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return version;
    }

    /// <inheritdoc />
    public Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CompletionReply.Empty(Status.Revision, 0));
    }

    /// <inheritdoc />
    public Task<AnalysisReply> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AnalysisReply(request.DocumentVersion, Status.Revision, 0, 0, null, false, []));
    }

    /// <inheritdoc />
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken) =>
        Task.FromException<HandleReply>(new ReplEngineException("the execution host is unavailable") { ExitCode = 3 });

    /// <inheritdoc />
    public Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken) =>
        Task.FromException<HandleReply>(new ReplEngineException("the execution host is unavailable") { ExitCode = 3 });

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
