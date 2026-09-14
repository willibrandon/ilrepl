using System.Collections;
using System.Collections.Concurrent;

namespace IlRepl.Tests.Engine;

/// <summary>
/// A real concurrent dictionary traps user collection dispatch while retaining extra stored state.
/// </summary>
internal sealed class ConcurrentObservationDictionary : ConcurrentDictionary<string, object?>,
    ICollection, IDictionary, IEnumerable<KeyValuePair<string, object?>>
{
    /// <summary>
    /// Stores an alias to one of the logical dictionary values.
    /// </summary>
    public object? Extra;

    /// <summary>
    /// Rejects a user-defined comparer getter during observation.
    /// </summary>
    public new IEqualityComparer<string> Comparer => throw new InvalidOperationException("user comparer getter ran at count " + base.Count);

    /// <summary>
    /// Rejects a user-defined count getter during observation.
    /// </summary>
    public new int Count => throw new InvalidOperationException("user count getter ran at count " + base.Count);

    /// <inheritdoc/>
    int ICollection.Count => throw new InvalidOperationException("user interface count getter ran");

    /// <inheritdoc/>
    void ICollection.CopyTo(Array array, int index) => throw new InvalidOperationException("user collection copy ran");

    /// <inheritdoc/>
    IDictionaryEnumerator IDictionary.GetEnumerator() => throw new InvalidOperationException("user dictionary enumeration ran");

    /// <inheritdoc/>
    IEnumerator<KeyValuePair<string, object?>> IEnumerable<KeyValuePair<string, object?>>.GetEnumerator() =>
        throw new InvalidOperationException("user generic dictionary enumeration ran");

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => throw new InvalidOperationException("user enumeration ran");
}
