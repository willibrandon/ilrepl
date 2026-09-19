using System.Collections;

namespace IlRepl.Tests.Engine;

/// <summary>
/// A real object comparer records construction calls and rejects callbacks during observation.
/// </summary>
internal sealed class HashtableObservationComparer : IEqualityComparer
{
    /// <summary>
    /// The stored hash setting.
    /// </summary>
    public int Salt = 17;

    /// <summary>
    /// The number of equality, hashing, or formatting callbacks.
    /// </summary>
    public int Calls;

    /// <summary>
    /// Whether callbacks must fail during capture.
    /// </summary>
    public bool Reject;

    /// <summary>
    /// Compares string contents while recording actual comparer use.
    /// </summary>
    /// <param name="x">The first key.</param>
    /// <param name="y">The second key.</param>
    /// <returns>Whether the ordinal string values match.</returns>
    public new bool Equals(object? x, object? y)
    {
        Record();
        return string.Equals((string?)x, (string?)y, StringComparison.Ordinal);
    }

    /// <summary>
    /// Produces collisions to exercise genuine equality during construction.
    /// </summary>
    /// <param name="obj">The key being hashed.</param>
    /// <returns>The configured salt.</returns>
    public int GetHashCode(object obj)
    {
        Record();
        return Salt;
    }

    /// <summary>
    /// Records and rejects user formatting during capture.
    /// </summary>
    /// <returns>A constant representation when callbacks are enabled.</returns>
    public override string ToString()
    {
        Record();
        return "stored comparer";
    }

    private void Record()
    {
        Calls++;
        if (Reject)
        {
            throw new InvalidOperationException("observation invoked the user's comparer");
        }
    }
}
