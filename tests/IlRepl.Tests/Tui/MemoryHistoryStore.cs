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
    /// The entries in the store from before this store wrote any; a load returns them followed
    /// by what was appended.
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
    public int Written => Appended.Count;

    /// <inheritdoc />
    public async Task<HistorySnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        // The read happens when it is asked for; the wait is the answer being slow to arrive.
        Loads++;
        var snapshot = new HistorySnapshot([.. Stored, .. Appended], Appended.Count);
        if (HoldLoad is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return snapshot;
    }

    /// <inheritdoc />
    public Task AppendAsync(string entry, CancellationToken cancellationToken)
    {
        Appended.Add(entry);
        return Task.CompletedTask;
    }
}
