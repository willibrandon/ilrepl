namespace IlRepl.Tui;

/// <summary>
/// Where history is kept between runs: a file on the desktop, the browser's own database on the
/// docs site. A store that cannot do its job says why and the prompt goes on without it.
/// </summary>
public interface IHistoryStore
{
    /// <summary>
    /// Why the store is not working, or null while it is.
    /// </summary>
    string? Problem { get; }

    /// <summary>
    /// Reads every entry, oldest first.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entries, or none when there is nothing to read.</returns>
    Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Adds one entry. The task completes once the entry is durable.
    /// </summary>
    /// <param name="entry">The entry, lines separated by newlines.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the entry is written, or when the store has given up and set <see cref="Problem"/>.</returns>
    Task AppendAsync(string entry, CancellationToken cancellationToken);
}
