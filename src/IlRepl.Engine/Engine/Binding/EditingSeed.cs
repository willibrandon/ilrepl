namespace IlRepl.Engine.Binding;

/// <summary>
/// The immutable live-session input to a preview, with runtime ownership confined to its metadata lease.
/// </summary>
/// <param name="Snapshot">The leased committed metadata.</param>
/// <param name="Definitions">The committed definition recipes.</param>
/// <param name="CellDeclarations">Persistent cell declarations.</param>
/// <param name="CellLines">The pending cell body.</param>
/// <param name="OpenLines">The open class or method's accepted source.</param>
/// <param name="TypeArguments">The concrete arguments assigned to the cell's generic parameters.</param>
/// <param name="InBlockComment">The current lexical state.</param>
/// <param name="Revision">The captured completion revision.</param>
internal sealed record EditingSeed(
    BindingSnapshot Snapshot,
    IReadOnlyList<EditingDefinition> Definitions,
    IReadOnlyList<string> CellDeclarations,
    IReadOnlyList<string> CellLines,
    IReadOnlyList<string> OpenLines,
    IReadOnlyList<TypeSymbol>? TypeArguments,
    bool InBlockComment,
    long Revision) : IDisposable
{
    /// <summary>
    /// Releases the captured assemblies when the editing view is discarded.
    /// </summary>
    public void Dispose() => Snapshot.Dispose();
}
