using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// A history store that keeps its entries in a list, for tests of what the prompt does with a store.
/// </summary>
internal sealed class MemoryHistoryStore : IHistoryStore
{
    /// <summary>
    /// The entries appended so far.
    /// </summary>
    public List<string> Appended { get; } = [];

    /// <summary>
    /// The entries a load returns.
    /// </summary>
    public List<string> Stored { get; } = [];

    /// <summary>
    /// How many loads have happened.
    /// </summary>
    public int Loads { get; private set; }

    /// <inheritdoc />
    public string? Problem { get; set; }

    /// <summary>
    /// When set, a load waits for this before it answers, so a test can act while history is
    /// still being read.
    /// </summary>
    public TaskCompletionSource? HoldLoad { get; set; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken)
    {
        Loads++;
        if (HoldLoad is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Stored.ToList();
    }

    /// <inheritdoc />
    public Task AppendAsync(string entry, CancellationToken cancellationToken)
    {
        Appended.Add(entry);
        return Task.CompletedTask;
    }
}
