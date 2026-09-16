namespace IlRepl.Engine;

/// <summary>
/// Keeps immutable edit dependency graphs alive alongside the session that owns them.
/// </summary>
public sealed partial class TypeResolver
{
    private readonly List<TypeResolver> _snapshotResolvers = [];

    /// <summary>
    /// The effective verified native library bindings retained by this dependency graph.
    /// </summary>
    internal IReadOnlyDictionary<string, string> NativeLibraries => _context.NativeLibraries;

    /// <summary>
    /// Creates an isolated resolver whose dependency bytes cannot be replaced by a later project rebuild.
    /// </summary>
    /// <param name="images">The original managed dependency images.</param>
    /// <param name="nativeLibraries">The original native bindings at their verified owned paths.</param>
    /// <returns>The resolver disposed with its owning session.</returns>
    internal TypeResolver CreateSnapshotResolver(IEnumerable<byte[]> images, IReadOnlyDictionary<string, string> nativeLibraries)
    {
        var resolver = new TypeResolver();
        try
        {
            if (resolver._context.IsCollectible) resolver._context.Unload();
            resolver._context = new ReferenceLoadContext(_context, reusePreviousImages: false);
            foreach (var (name, path) in nativeLibraries) resolver.RegisterNative(name, path);
            var frozen = images.ToArray();
            resolver.RegisterImages(frozen);
            foreach (var image in frozen)
            {
                resolver.LoadImage(image);
            }

            _snapshotResolvers.Add(resolver);
            return resolver;
        }
        catch
        {
            resolver.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Makes newly added references available to revisions while preserving every original assembly identity.
    /// </summary>
    /// <param name="live">The session's current dependency resolver.</param>
    internal void AddSnapshotReferences(TypeResolver live)
    {
        var existing = _extra.Select(assembly => assembly.GetName().Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = live._extra.Where(assembly => !existing.Contains(assembly.GetName().Name))
            .Select(assembly => live.TryGetImage(assembly, out var image) ? image : null).OfType<byte[]>().ToArray();
        RegisterImages(additions);
        foreach (var image in additions)
        {
            LoadImage(image);
        }
    }
}
