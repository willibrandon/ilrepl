namespace IlRepl.Protocol;

/// <summary>
/// A replayable editable method document returned by an edit command.
/// </summary>
/// <param name="Identity">The identity used for source diagnostics and editor recovery.</param>
/// <param name="Name">The edit name.</param>
/// <param name="Source">The complete .edit block to place in the multiline editor.</param>
/// <param name="OriginalReference">The source method reference.</param>
/// <param name="BaselineFingerprint">The identity of the immutable original.</param>
/// <param name="Revision">The last successfully committed revision.</param>
public sealed record EditDocument(
    string Identity,
    string Name,
    string Source,
    string OriginalReference,
    string BaselineFingerprint,
    int Revision)
{
    /// <summary>
    /// The exact source symbols, locations, accessibility, and binding decisions for this revision.
    /// </summary>
    public IReadOnlyList<EditDependency> Dependencies { get; init; } = [];

    /// <summary>
    /// The preflight blockers that the editable source can correct before its first commit.
    /// </summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}
