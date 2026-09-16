using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Reopens immutable edit originals with their source-time dependencies in a separately owned resolution context.
/// </summary>
public sealed partial class Session
{
    /// <summary>
    /// Restores an original independently of rebuilt live dependencies before replaying its saved revisions.
    /// </summary>
    /// <param name="snapshot">The immutable original and source-time binding metadata.</param>
    /// <param name="images">The verified dependency images for the original.</param>
    /// <param name="nativeLibraries">The verified owned native paths retained by the original.</param>
    /// <returns>The restored edit awaiting its previously accepted revisions.</returns>
    internal MethodEdit RestoreEditSnapshot(SessionEditSnapshot snapshot, IEnumerable<byte[]> images,
        IReadOnlyDictionary<string, string> nativeLibraries)
    {
        var resolver = Resolver.CreateSnapshotResolver(images, nativeLibraries);
        var family = ImportedMethodFamily.RestoreSnapshot(snapshot, this, resolver);
        if (family.Problems.Count == 0) family.Compile();
        var edit = new MethodEdit(snapshot.Name, snapshot.Reference, family) { Fingerprint = snapshot.Fingerprint };
        _edits.Add(edit);
        CompletionRevision++;
        return edit;
    }

    /// <summary>
    /// Refreshes visible live types in a frozen parsing session before compiling a later edit revision.
    /// </summary>
    /// <param name="types">The current user-visible type table.</param>
    internal void ReplaceSnapshotTypes(TypeTable types) => _typeTable = types.Clone();
}
