using System.Collections;

namespace IlRepl.Tests.Engine;

/// <summary>
/// A real hash set whose user enumeration must never run during structural observation.
/// </summary>
internal sealed class ObservationSet : HashSet<object>, IEnumerable<object>
{
    /// <summary>
    /// Extra stored state that aliases a set element.
    /// </summary>
    public object? Extra;

    /// <inheritdoc/>
    IEnumerator<object> IEnumerable<object>.GetEnumerator() => throw new InvalidOperationException("user set enumeration ran");

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => throw new InvalidOperationException("user enumeration ran");
}
