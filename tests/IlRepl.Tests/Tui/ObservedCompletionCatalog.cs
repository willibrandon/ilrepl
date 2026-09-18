using System.Collections;
using IlRepl.Protocol;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Observes enumeration of an unchanged real completion catalog so a test can synchronize an actual assembly load.
/// </summary>
internal sealed class ObservedCompletionCatalog(IReadOnlyList<CompletionItem> catalog, Action enumerating)
    : IReadOnlyList<CompletionItem>
{
    private Action? _enumerating = enumerating;

    /// <inheritdoc />
    public int Count => catalog.Count;

    /// <inheritdoc />
    public CompletionItem this[int index] => catalog[index];

    /// <inheritdoc />
    public IEnumerator<CompletionItem> GetEnumerator()
    {
        Interlocked.Exchange(ref _enumerating, null)?.Invoke();
        return catalog.GetEnumerator();
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
