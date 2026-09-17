namespace IlRepl.Engine;

/// <summary>
/// Selects assembly lifetime for an isolated compilation without changing other sessions.
/// </summary>
public sealed class AssemblyLifetimeScope : IDisposable
{
    private static readonly AsyncLocal<bool?> Current = new();
    private readonly bool? _previous;

    /// <summary>
    /// Enters the requested lifetime for the current asynchronous execution context.
    /// </summary>
    /// <param name="collectible">Whether generated assemblies may unload.</param>
    public AssemblyLifetimeScope(bool collectible)
    {
        _previous = Current.Value;
        Current.Value = collectible;
    }

    /// <summary>
    /// The effective lifetime, retaining the usual session default outside an inspection.
    /// </summary>
    internal static bool Collectible => !OperatingSystem.IsBrowser() && (Current.Value ?? true);

    /// <summary>
    /// Restores the enclosing compilation lifetime.
    /// </summary>
    public void Dispose() => Current.Value = _previous;
}
