namespace IlRepl.Protocol;

/// <summary>
/// A comparison between an edit's captured baseline and its latest committed instructions and metadata.
/// </summary>
/// <param name="Name">The edit name.</param>
/// <param name="BaselineFingerprint">The immutable original's identity.</param>
/// <param name="Revision">The committed revision compared.</param>
/// <param name="Raw">Whether encoding and generated names are shown without normalization.</param>
/// <param name="Rows">The ordered comparison rows.</param>
public sealed record EditDiff(string Name, string BaselineFingerprint, int Revision, bool Raw, IReadOnlyList<EditDiffRow> Rows)
{
    /// <summary>
    /// Whether any instruction, stack, or metadata differs under the selected normalization.
    /// </summary>
    public bool HasChanges => Rows.Any(row => row.Kind != "equal");
}
