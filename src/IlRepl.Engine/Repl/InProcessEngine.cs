using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// An engine that runs <see cref="ReplCore"/> in the current process. The browser build uses it;
/// the Native AOT tool talks to the same core through the host process instead.
/// </summary>
public sealed class InProcessEngine : IReplEngine
{
    private readonly ReplCore _core;

    /// <summary>
    /// Initializes an engine over a new session.
    /// </summary>
    public InProcessEngine() : this(new ReplCore())
    {
    }

    /// <summary>
    /// Initializes an engine over the given core.
    /// </summary>
    /// <param name="core">The REPL core.</param>
    public InProcessEngine(ReplCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
        Status = core.Status;
    }

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> Catalog => Completer.Catalog;

    /// <inheritdoc />
    public SessionStatus Status { get; private set; }

    /// <inheritdoc />
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        var result = _core.Handle(line);
        var lines = _core.Transcript.Lines.ToArray();
        _core.Transcript.Clear();
        Status = _core.Status;
        return Task.FromResult(new HandleReply(result.Succeeded, result.QuitRequested, lines, Status));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
