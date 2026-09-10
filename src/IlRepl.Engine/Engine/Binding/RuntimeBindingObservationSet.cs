namespace IlRepl.Engine.Binding;

/// <summary>
/// Stores copied type-reference bindings under the lifetime of their requesting assembly.
/// </summary>
internal sealed class RuntimeBindingObservationSet
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, TypeSymbol> _types = [];
    private long _version;

    /// <summary>
    /// The version of this requesting assembly's observed reference bindings.
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Adds newly observed references while preserving the CLR's first binding for each token.
    /// </summary>
    /// <param name="types">The references resolved by a real operation.</param>
    /// <returns>Whether the known binding graph changed.</returns>
    public bool Add(IReadOnlyDictionary<int, TypeSymbol> types)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var (token, type) in types)
            {
                changed |= _types.TryAdd(token, type);
            }

            if (changed)
            {
                Interlocked.Increment(ref _version);
            }
        }

        return changed;
    }

    /// <summary>
    /// Copies the observed references without retaining runtime assemblies or mutable registry state.
    /// </summary>
    /// <returns>The immutable snapshot's private copy.</returns>
    public IReadOnlyDictionary<int, TypeSymbol> Capture()
    {
        lock (_gate)
        {
            return new Dictionary<int, TypeSymbol>(_types);
        }
    }
}
