using System.Collections;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Records attempts to inspect a user-defined lookup through its executable interface members.
/// </summary>
internal sealed class UserLookupObservationProbe : ILookup<string, int>
{
    private readonly Action _called;

    /// <summary>
    /// Creates a lookup that reports every executable interface access.
    /// </summary>
    /// <param name="called">The callback that records an access.</param>
    internal UserLookupObservationProbe(Action called) => _called = called;

    /// <summary>
    /// Invokes the probe if observation reads the lookup count.
    /// </summary>
    public int Count
    {
        get
        {
            _called();
            return 0;
        }
    }

    /// <summary>
    /// Invokes the probe if observation reads a lookup group.
    /// </summary>
    /// <param name="key">The requested group key.</param>
    public IEnumerable<int> this[string key]
    {
        get
        {
            _called();
            return [];
        }
    }

    /// <summary>
    /// Invokes the probe if observation searches for a key.
    /// </summary>
    /// <param name="key">The requested key.</param>
    /// <returns>Always false.</returns>
    public bool Contains(string key)
    {
        _called();
        return false;
    }

    /// <summary>
    /// Invokes the probe if observation enumerates groups.
    /// </summary>
    /// <returns>An empty grouping enumerator.</returns>
    public IEnumerator<IGrouping<string, int>> GetEnumerator()
    {
        _called();
        return Enumerable.Empty<IGrouping<string, int>>().GetEnumerator();
    }

    /// <summary>
    /// Invokes the probe if observation uses nongeneric enumeration.
    /// </summary>
    /// <returns>An empty grouping enumerator.</returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
