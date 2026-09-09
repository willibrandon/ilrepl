namespace IlRepl.Protocol;

/// <summary>
/// A confirmed page, bound to one document and one engine snapshot.
/// </summary>
/// <param name="Kind">The kind of site.</param>
/// <param name="ReplaceStart">The first UTF-16 offset replaced on the caret's line.</param>
/// <param name="ReplaceLength">The length replaced, including an existing suffix.</param>
/// <param name="Items">Candidates in host ranking order.</param>
/// <param name="Cursor">The next-page token; an empty page can still have one.</param>
/// <param name="Total">The confirmed count, or -1 for a rejected cursor.</param>
/// <param name="TotalIsProvisional">Whether more candidates remain to be confirmed.</param>
/// <param name="Revision">The captured session revision.</param>
/// <param name="QueryId">The opaque full-query identity.</param>
/// <param name="BindingEpoch">The captured loaded-binding epoch.</param>
/// <param name="Owners">The generic owners of this argument page.</param>
public sealed record CompletionReply(
    CompletionKind Kind,
    int ReplaceStart,
    int ReplaceLength,
    IReadOnlyList<CompletionItem> Items,
    string? Cursor,
    int Total,
    bool TotalIsProvisional,
    long Revision,
    string QueryId,
    long BindingEpoch,
    IReadOnlyList<string> Owners)
{
    /// <summary>
    /// The searchable assembly-load version captured before building this page.
    /// </summary>
    public long AssemblyVersion { get; init; }

    /// <summary>
    /// The maximum number of confirmed rows per page.
    /// </summary>
    public const int PageSize = 64;

    /// <summary>
    /// An empty answer for the supplied snapshot.
    /// </summary>
    /// <param name="revision">The semantic revision.</param>
    /// <param name="epoch">The binding epoch.</param>
    /// <returns>An empty page.</returns>
    public static CompletionReply Empty(long revision, long epoch) =>
        new(CompletionKind.None, 0, 0, [], null, 0, false, revision, "", epoch, []);

    /// <summary>
    /// Rejects a cursor whose query is no longer current.
    /// </summary>
    /// <param name="revision">The current semantic revision.</param>
    /// <param name="epoch">The current binding epoch.</param>
    /// <returns>A reply requiring the client to discard its old pages.</returns>
    public static CompletionReply CursorRejected(long revision, long epoch) => Empty(revision, epoch) with { Total = -1 };
}
