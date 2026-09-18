using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Retains one explicit selection while an assembly change obtains a newly bound completion page.
/// </summary>
/// <param name="Key">The unchanged editor and session identity at the acceptance key press.</param>
/// <param name="Item">The displayed candidate that must be confirmed again before insertion.</param>
internal sealed record CompletionAcceptance(CompletionRequestKey Key, CompletionItem Item)
{
    /// <summary>
    /// Permits catalog changes while rejecting edits, movement, selections, and semantic session changes.
    /// </summary>
    internal bool Matches(CompletionRequestKey current) => Key.Version == current.Version && Key.Revision == current.Revision
        && Key.Document.Line == current.Document.Line && Key.Document.Caret == current.Document.Caret
        && Key.Selection == current.Selection && Key.AnchorVersion == current.AnchorVersion;

    /// <summary>
    /// Identifies the same freshly bound spelling without reusing an old generic continuation token.
    /// </summary>
    internal bool Selects(CompletionItem current) => Item.Kind == current.Kind && Item.Name == current.Name
        && Item.InsertText == current.InsertText && Item.Detail == current.Detail && Item.Description == current.Description
        && Item.FullDetail == current.FullDetail && Item.Owner == current.Owner && Item.TakesOperand == current.TakesOperand
        && Item.Continues == current.Continues && Item.CaretOffset == current.CaretOffset;
}
