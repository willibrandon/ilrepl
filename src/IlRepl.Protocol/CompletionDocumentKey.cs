namespace IlRepl.Protocol;

/// <summary>
/// Identifies a complete completion document, caret and continuation selection by immutable structural equality.
/// </summary>
public sealed class CompletionDocumentKey : IEquatable<CompletionDocumentKey>
{
    private readonly int _hashCode;

    /// <summary>
    /// Copies the whole request while deliberately excluding its page cursor.
    /// </summary>
    /// <param name="request">The document query.</param>
    public CompletionDocumentKey(CompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Lines);
        ArgumentNullException.ThrowIfNull(request.Anchors);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(request.Line, request.Lines.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Caret);
        var lines = request.Lines.ToArray();
        foreach (var line in lines)
        {
            ArgumentNullException.ThrowIfNull(line);
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Caret, lines[request.Line].Length);
        var anchors = request.Anchors.ToArray();
        foreach (var anchor in anchors)
        {
            ArgumentNullException.ThrowIfNull(anchor);
            ArgumentNullException.ThrowIfNull(anchor.Token);
        }

        Lines = Array.AsReadOnly(lines);
        Anchors = Array.AsReadOnly(anchors);
        Line = request.Line;
        Caret = request.Caret;
        Explicit = request.Explicit;
        var hash = new HashCode();
        foreach (var line in Lines)
        {
            hash.Add(line, StringComparer.Ordinal);
        }

        foreach (var anchor in Anchors)
        {
            hash.Add(anchor);
        }

        hash.Add(Line);
        hash.Add(Caret);
        hash.Add(Explicit);
        _hashCode = hash.ToHashCode();
    }

    /// <summary>
    /// Every line, including suffixes and future label declarations.
    /// </summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>
    /// The zero-based caret line.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// The UTF-16 caret offset within its line.
    /// </summary>
    public int Caret { get; }

    /// <summary>
    /// The selected generic definitions and their document spans, compared by value.
    /// </summary>
    public IReadOnlyList<ContinuationAnchor> Anchors { get; }

    /// <summary>
    /// Whether the query was explicitly requested.
    /// </summary>
    public bool Explicit { get; }

    /// <summary>
    /// Reuses the immutable document with the server's supplied paging token.
    /// </summary>
    /// <param name="cursor">The page cursor, or null for an initial query.</param>
    /// <returns>The corresponding request.</returns>
    public CompletionRequest Request(string? cursor = null) => new(Lines, Line, Caret, cursor, Anchors, Explicit);

    /// <inheritdoc/>
    public bool Equals(CompletionDocumentKey? other) => ReferenceEquals(this, other)
        || other is not null && Line == other.Line && Caret == other.Caret && Explicit == other.Explicit
            && Lines.SequenceEqual(other.Lines, StringComparer.Ordinal) && Anchors.SequenceEqual(other.Anchors);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CompletionDocumentKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _hashCode;
}
