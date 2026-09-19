namespace IlRepl.Tests.Engine;

/// <summary>
/// A real comparer whose observable state and callback traps detect user code invoked during structural observation.
/// </summary>
internal sealed class ImmutableObservationComparer : IEqualityComparer<string>
{
    /// <summary>
    /// A stored comparer setting with no dependency on randomized string hashes.
    /// </summary>
    public int Salt;

    /// <summary>
    /// Counts equality, hashing, and formatting calls.
    /// </summary>
    public int Callbacks;

    /// <summary>
    /// Causes any callback to fail after the real immutable collection has been constructed.
    /// </summary>
    public bool RejectCallbacks;

    /// <summary>
    /// Compares actual string values while recording callback execution.
    /// </summary>
    /// <param name="x">The first string.</param>
    /// <param name="y">The second string.</param>
    /// <returns>Whether the ordinal contents match.</returns>
    public bool Equals(string? x, string? y)
    {
        Record();
        return string.Equals(x, y, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns a deliberately colliding hash to exercise the collection's genuine equality path during construction.
    /// </summary>
    /// <param name="obj">The string being hashed.</param>
    /// <returns>The stored hash salt.</returns>
    public int GetHashCode(string obj)
    {
        Record();
        return Salt;
    }

    /// <summary>
    /// Rejects formatting while observation is active.
    /// </summary>
    /// <returns>A constant representation if callbacks are enabled.</returns>
    public override string ToString()
    {
        Record();
        return "custom comparer";
    }

    private void Record()
    {
        Callbacks++;
        if (RejectCallbacks)
        {
            throw new InvalidOperationException("observation invoked the user's comparer");
        }
    }
}
