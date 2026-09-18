using IlRepl.Protocol;

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
    /// Original source coordinates for each accepted cell declaration, when available.
    /// </summary>
    internal IReadOnlyList<AnalysisLocation?> CellDeclarationLocations { get; init; } = [];

    /// <summary>
    /// Original source coordinates for each accepted cell instruction or body directive, when available.
    /// </summary>
    internal IReadOnlyList<AnalysisLocation?> CellLocations { get; init; } = [];

    /// <summary>
    /// Original source coordinates for accepted instructions in the current open method or member body.
    /// </summary>
    internal IReadOnlyList<AnalysisLocation?> OpenLocations { get; init; } = [];

    /// <summary>
    /// The pinned original method definitions available to edit submissions.
    /// </summary>
    internal IReadOnlyList<EditingMethodEdit> Edits { get; init; } = [];

    /// <summary>
    /// The assembly observation version captured with this immutable source snapshot.
    /// </summary>
    internal long AssemblyVersion { get; init; }

    /// <summary>
    /// Creates a separately disposable lease without copying accepted source or live runtime objects.
    /// </summary>
    internal EditingSeed Lease() => this with { Snapshot = Snapshot.Lease() };

    /// <summary>
    /// Releases the captured assemblies when the editing view is discarded.
    /// </summary>
    public void Dispose() => Snapshot.Dispose();
}
