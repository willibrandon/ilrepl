namespace IlRepl.Protocol;

/// <summary>
/// Locates operand completion ranges using the same lexical and grammatical rules as the tokenizer.
/// </summary>
public sealed class CaretClassifier
{
    private readonly CilTokenizer _tokenizer;

    /// <summary>
    /// Initializes a classifier over a tokenizer's vocabulary.
    /// </summary>
    /// <param name="tokenizer">The tokenizer.</param>
    public CaretClassifier(CilTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        _tokenizer = tokenizer;
    }

    /// <summary>
    /// Classifies the caret in a line.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="caret">The caret offset, 0 to the line's length.</param>
    /// <param name="inBlockComment">Whether a <c>/*</c> from an earlier line is open at the line's start.</param>
    /// <returns>The site, <see cref="CompletionSite.None"/> when nothing can be listed there.</returns>
    public CompletionSite Classify(string line, int caret, bool inBlockComment)
    {
        ArgumentNullException.ThrowIfNull(line);
        caret = Math.Clamp(caret, 0, line.Length);
        var comment = inBlockComment;
        foreach (var segment in CilLexer.Segments(line, ref comment))
        {
            var closedBlock = segment.Kind == CilSegmentKind.BlockComment
                && (segment.Length >= 4 || segment.Start == 0 && inBlockComment)
                && line.AsSpan(segment.Start, segment.Length).EndsWith("*/", StringComparison.Ordinal);
            if (segment.Kind is CilSegmentKind.LineComment or CilSegmentKind.BlockComment
                && (segment.Start < caret || caret == 0) && (caret < segment.End || caret == segment.End && !closedBlock))
            {
                return CompletionSite.None with { Caret = caret };
            }
        }

        comment = inBlockComment;
        var lexemes = CilScanner.Scan(line, ref comment);
        var code = new List<CilLexeme>(lexemes.Count);
        foreach (var lexeme in lexemes)
        {
            if (lexeme.Kind == CilLexemeKind.Comment)
            {
                continue;
            }

            if (lexeme.Kind is CilLexemeKind.String && lexeme.Start < caret && caret < lexeme.End)
            {
                return CompletionSite.None with { Caret = caret };
            }

            code.Add(lexeme);
        }

        var reader = new CilLineReader(_tokenizer, line, code, []);
        var site = new CaretWalk(_tokenizer, reader, line, caret).Line();
        return site is null || site.Kind == CompletionSiteKind.None ? CompletionSite.None with { Caret = caret } : site with
        {
            Caret
            = caret
        };
    }
}
