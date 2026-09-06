namespace IlRepl.Protocol;

/// <summary>
/// Prefix completion over a catalog of opcodes and commands. The front-end runs this locally
/// on every keystroke; the catalog itself comes from the host once.
/// </summary>
public static class CatalogCompleter
{
    /// <summary>
    /// Returns the catalog entries whose name starts with <paramref name="word"/>. A word that
    /// starts with a dot matches commands; anything else matches opcodes.
    /// </summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="word">The first word typed so far.</param>
    /// <returns>The matches in catalog order, or an empty list for an empty word.</returns>
    public static IReadOnlyList<CompletionItem> Complete(IReadOnlyList<CompletionItem> catalog, string word)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(word);
        if (word.Length == 0)
        {
            return [];
        }

        var commands = word.StartsWith('.');
        var matches = new List<CompletionItem>();
        foreach (var item in catalog)
        {
            if (item.Name.StartsWith('.') == commands && item.Name.StartsWith(word, StringComparison.Ordinal))
            {
                matches.Add(item);
            }
        }

        return matches;
    }
}
