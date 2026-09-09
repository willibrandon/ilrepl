using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Holds confirmed rows for one local query and accepts pages only from the same host binding snapshot.
/// </summary>
/// <param name="Key">The immutable local query identity.</param>
/// <param name="Reply">The confirmed rows and host query stamps.</param>
public sealed record CompletionSnapshot(CompletionRequestKey Key, CompletionReply Reply)
{
    /// <summary>
    /// Appends a page after checking the host's complete query identity.
    /// </summary>
    /// <param name="page">The next page.</param>
    /// <returns>The combined snapshot, or null when the page belongs to another query.</returns>
    public CompletionSnapshot? Append(CompletionReply page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Total < 0 || page.QueryId != Reply.QueryId || page.Revision != Reply.Revision
            || page.BindingEpoch != Reply.BindingEpoch || page.ReplaceStart != Reply.ReplaceStart
            || page.ReplaceLength != Reply.ReplaceLength ? null
            : this with { Reply = page with { Items = Reply.Items.Concat(page.Items).ToArray() } };
    }

    /// <summary>
    /// Hides a single completed spelling while keeping generic continuations available.
    /// </summary>
    /// <returns>The visible confirmed rows.</returns>
    public IReadOnlyList<CompletionItem> Visible()
    {
        var line = Key.Document.Lines[Key.Document.Line];
        var start = Reply.ReplaceStart;
        var length = Reply.ReplaceLength;
        if (start < 0 || length < 0 || start > line.Length - length)
        {
            return [];
        }

        return Reply.Cursor is null && Reply.Items.Count == 1 && !Reply.Items[0].Continues
            && line.AsSpan(start, length).SequenceEqual(Reply.Items[0].InsertText) ? [] : Reply.Items;
    }
}
