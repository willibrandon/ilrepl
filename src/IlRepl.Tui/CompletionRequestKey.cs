using Hex1b.Documents;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Identifies the complete local document, caret, selection and live session used by a completion request.
/// </summary>
/// <param name="Document">The immutable document query and structurally compared anchors.</param>
/// <param name="Version">The editor's document version.</param>
/// <param name="Revision">The live session's semantic revision.</param>
/// <param name="Site">The syntax-defined completion site.</param>
/// <param name="Selection">The selection, when one is active.</param>
/// <param name="AnchorVersion">The local anchored-selection version.</param>
/// <param name="Cursor">The server's optional next-page token.</param>
public sealed record CompletionRequestKey(
    CompletionDocumentKey Document, long Version, long Revision, CompletionSite Site, DocumentRange? Selection,
    long AnchorVersion, string? Cursor = null)
{
    /// <summary>
    /// The searchable assembly-load version observed when this request started.
    /// </summary>
    public long AssemblyVersion { get; init; }

    /// <summary>
    /// Compares all query components except the requested page cursor.
    /// </summary>
    /// <param name="other">The other request.</param>
    /// <returns>Whether both requests belong to the same local query.</returns>
    public bool SameQuery(CompletionRequestKey? other) =>
        other is not null && (this with { Cursor = null }) == (other with { Cursor = null });
}
