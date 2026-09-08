namespace IlRepl.Tui;

/// <summary>
/// The entries the prompt walks with Up and Down. While browsing, each entry is a working copy
/// the user may edit without changing the entry itself, and the buffer the browsing started from
/// is kept at the newest end, as prompt_toolkit keeps it. A new entry ends browsing.
/// </summary>
public sealed class PromptHistory
{
    /// <summary>
    /// How many entries are kept in memory.
    /// </summary>
    public const int MaxEntries = 1000;

    private readonly List<string> _entries = [];
    private readonly IHistoryStore? _store;
    private List<string>? _working;
    private int _index;

    /// <summary>
    /// Initializes a history, empty or from entries already known.
    /// </summary>
    /// <param name="store">Where entries are kept between runs, or null to keep none.</param>
    /// <param name="entries">Entries to start with, oldest first.</param>
    public PromptHistory(IHistoryStore? store = null, IEnumerable<string>? entries = null)
    {
        _store = store;
        if (entries is not null)
        {
            _entries.AddRange(entries);
            Trim();
        }

        _index = _entries.Count;
    }

    /// <summary>
    /// The entries, oldest first.
    /// </summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>
    /// Whether Up or Down has moved off the buffer the user was typing.
    /// </summary>
    public bool Browsing => _working is not null;

    /// <summary>
    /// Why the store is not working, or null while it is or when there is none.
    /// </summary>
    public string? Problem => _store?.Problem;

    /// <summary>
    /// Reads the entries the store holds, replacing any known so far.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the entries are in.</returns>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return;
        }

        var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        _entries.Clear();
        _entries.AddRange(loaded);
        Trim();
        Reset();
    }

    /// <summary>
    /// Adds an entry and ends browsing. Whitespace and a repeat of the newest entry are not added.
    /// </summary>
    /// <param name="entry">The entry, lines separated by newlines.</param>
    /// <returns>True when the entry was new.</returns>
    public bool Add(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var text = entry.TrimEnd('\n', '\r');
        Reset();
        if (text.Trim().Length == 0 || (_entries.Count > 0 && _entries[^1] == text))
        {
            return false;
        }

        _entries.Add(text);
        Trim();
        _index = _entries.Count;
        return true;
    }

    /// <summary>
    /// Adds an entry and, when it was new, writes it to the store before returning.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>True when the entry was new.</returns>
    public async Task<bool> AddAsync(string entry, CancellationToken cancellationToken)
    {
        if (!Add(entry))
        {
            return false;
        }

        if (_store is not null)
        {
            await _store.AppendAsync(entry.TrimEnd('\n', '\r'), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Moves to the previous entry, keeping the text the buffer holds now as the working copy of
    /// the place it came from.
    /// </summary>
    /// <param name="current">The buffer's text now.</param>
    /// <returns>The text to show, or null at the oldest entry.</returns>
    public string? Back(string current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (_index == 0)
        {
            return null;
        }

        _working ??= [.. _entries, current];
        _working[_index] = current;
        _index--;
        return _working[_index];
    }

    /// <summary>
    /// Moves to the next entry, or back to the buffer the browsing started from.
    /// </summary>
    /// <param name="current">The buffer's text now.</param>
    /// <returns>The text to show, or null when not browsing.</returns>
    public string? Forward(string current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (_working is null || _index >= _working.Count - 1)
        {
            return null;
        }

        _working[_index] = current;
        _index++;
        var text = _working[_index];
        if (_index == _working.Count - 1)
        {
            Reset();
        }

        return text;
    }

    /// <summary>
    /// Ends browsing and forgets the working copies.
    /// </summary>
    public void Reset()
    {
        _working = null;
        _index = _entries.Count;
    }

    private void Trim()
    {
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
    }
}
