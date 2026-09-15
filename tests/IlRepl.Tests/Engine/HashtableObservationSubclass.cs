using System.Collections;

namespace IlRepl.Tests.Engine;

/// <summary>
/// A hashtable subclass retains real entries while rejecting virtual collection callbacks.
/// </summary>
internal sealed class HashtableObservationSubclass : Hashtable
{
    /// <summary>
    /// Extra state sharing a logical dictionary value.
    /// </summary>
    public object? Extra;

    /// <summary>
    /// Rejects virtual count inspection.
    /// </summary>
    public override int Count => throw new InvalidOperationException("user count getter ran");

    /// <summary>
    /// Rejects virtual synchronization inspection.
    /// </summary>
    public override object SyncRoot => throw new InvalidOperationException("user synchronization getter ran");

    /// <summary>
    /// Rejects virtual copying during structural capture.
    /// </summary>
    /// <param name="array">The requested destination.</param>
    /// <param name="arrayIndex">The requested start position.</param>
    public override void CopyTo(Array array, int arrayIndex) => throw new InvalidOperationException("user copy ran");

    /// <summary>
    /// Rejects virtual enumeration during structural capture.
    /// </summary>
    /// <returns>No user enumerator is permitted.</returns>
    public override IDictionaryEnumerator GetEnumerator() => throw new InvalidOperationException("user enumeration ran");
}
