using System.Collections;

namespace IlRepl.Tests.Engine;

/// <summary>
/// A real dictionary whose user enumeration must never run during structural observation.
/// </summary>
internal sealed class ObservationDictionary : Dictionary<string, object?>, IDictionary, IEnumerable<KeyValuePair<string, object?>>
{
    /// <summary>
    /// Extra stored state that aliases a dictionary value.
    /// </summary>
    public object? Extra;

    /// <inheritdoc/>
    IDictionaryEnumerator IDictionary.GetEnumerator() => throw new InvalidOperationException("user dictionary enumeration ran");

    /// <inheritdoc/>
    IEnumerator<KeyValuePair<string, object?>> IEnumerable<KeyValuePair<string, object?>>.GetEnumerator() =>
        throw new InvalidOperationException("user generic dictionary enumeration ran");

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => throw new InvalidOperationException("user enumeration ran");
}
