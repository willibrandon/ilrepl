using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Identifies one ranked query by its full document, site, session generation and captured assembly bindings.
/// </summary>
/// <param name="Session">The owning completer's lifetime identity.</param>
/// <param name="Revision">The live session revision captured before replay.</param>
/// <param name="BindingEpoch">The captured loaded-binding epoch.</param>
/// <param name="Document">The complete immutable document and selected generic anchors.</param>
/// <param name="Site">The syntax-defined completion site.</param>
internal sealed record CompletionQueryIdentity(
    Guid Session, long Revision, long BindingEpoch, CompletionDocumentKey Document, CompletionSite Site);
