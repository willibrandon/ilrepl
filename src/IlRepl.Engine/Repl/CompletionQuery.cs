using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Owns one immutable ranked candidate set and the confirmed pages already produced from it.
/// </summary>
internal sealed class CompletionQuery
{
    /// <summary>
    /// The complete identity that every subsequent page must match.
    /// </summary>
    public required CompletionQueryIdentity Identity { get; init; }

    /// <summary>
    /// The opaque identity returned to the client with each page.
    /// </summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The captured editing view used for every candidate confirmation.
    /// </summary>
    public required EditingView View { get; init; }

    /// <summary>
    /// The complete ranked candidate order before confirmation.
    /// </summary>
    public required IReadOnlyList<OperandCandidate> Candidates { get; init; }

    /// <summary>
    /// The kind shown in the palette title.
    /// </summary>
    public required CompletionKind Kind { get; init; }

    /// <summary>
    /// The query's cached type spelling confirmations.
    /// </summary>
    public required TypeSpeller Types { get; init; }

    /// <summary>
    /// The query's cached member spelling context.
    /// </summary>
    public required MemberSpeller Members { get; init; }

    /// <summary>
    /// Generic owners represented by this argument page.
    /// </summary>
    public IReadOnlyList<string> Owners { get; init; } = [];

    /// <summary>
    /// Member names whose matching candidates belong to different declaring constructions.
    /// </summary>
    public IReadOnlySet<string> AmbiguousNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The next candidate to examine, counting candidates rejected during confirmation too.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// The number of candidates confirmed in every page so far.
    /// </summary>
    public int Confirmed { get; set; }

    /// <summary>
    /// The only next-page token currently valid for this query.
    /// </summary>
    public string? Cursor { get; set; }
}
