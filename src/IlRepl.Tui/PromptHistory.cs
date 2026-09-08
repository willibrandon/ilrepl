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
    private readonly int _baseline;
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
        _baseline = store?.Written ?? 0;
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

        Load(await _store.LoadAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Takes what the store held when it was read. The store's writes since this session began
    /// are the session's own, and those the read holds are at its end.
    /// </summary>
    /// <param name="snapshot">What the store held.</param>
    public void Load(HistorySnapshot snapshot) => Load(snapshot.Entries, snapshot.Written - _baseline);

    /// <summary>
    /// Adds an entry and ends browsing. Whitespace and a repeat of the newest entry are not added.
    /// </summary>
    /// <param name="entry">The entry, lines separated by newlines.</param>
    /// <returns>True when the entry was new.</returns>
    public bool Add(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // The entry is the buffer as sent, a trailing blank line included: at the top level that
        // line runs the cell, and a recalled entry must do what the original did.
        var text = entry.Replace("\r\n", "\n", StringComparison.Ordinal);
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
            await _store.AppendAsync(entry.Replace("\r\n", "\n", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Writes an entry to the store, when there is one. The entry is durable when the task completes.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the entry is written.</returns>
    public async Task PersistAsync(string entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_store is not null)
        {
            await _store.AppendAsync(entry.Replace("\r\n", "\n", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes the stored entries, which go before whatever this session has added while they
    /// were being read, so a line submitted before the store answered stays recallable. The
    /// session's own writes that the store had taken before it was read are the last stored
    /// entries and are not added again; a stored run that merely reads the same is left alone.
    /// Browsing goes on where it was: the working copies and the draft the buffer held when it
    /// began stay.
    /// </summary>
    /// <param name="stored">The entries from the store, oldest first.</param>
    /// <param name="own">How many of them, at the end, this session wrote itself before the store was read.</param>
    public void Load(IEnumerable<string> stored, int own = 0)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var added = _entries.ToList();
        var copies = _working;
        var index = _index;
        _entries.Clear();
        _entries.AddRange(stored);

        // Where each of the session's entries sits now: the first own of them are already the
        // last own stored ones.
        own = Math.Clamp(own, 0, Math.Min(_entries.Count, added.Count));
        var positions = new int[added.Count];
        for (var i = 0; i < added.Count; i++)
        {
            if (i < own)
            {
                positions[i] = _entries.Count - own + i;
                continue;
            }

            _entries.Add(added[i]);
            positions[i] = _entries.Count - 1;
        }

        var removed = Math.Max(0, _entries.Count - MaxEntries);
        Trim();
        if (copies is null)
        {
            _index = _entries.Count;
            return;
        }

        var working = new List<string>(_entries) { copies[^1] };
        for (var i = 0; i < added.Count; i++)
        {
            var at = positions[i] - removed;
            if (at >= 0)
            {
                working[at] = copies[i];
            }
        }

        _working = working;
        _index = index < added.Count ? Math.Max(0, positions[index] - removed) : working.Count - 1;
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
