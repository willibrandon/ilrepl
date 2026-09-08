using Hex1b.Documents;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Colours the buffer with the tokenizer, line by line, carrying an open <c>/* */</c> from the
/// engine's state through the lines above the viewport. The result is cached by document version
/// and viewport, so the frames between keystrokes reuse it.
/// </summary>
public sealed class CilDecorationProvider : ITextDecorationProvider
{
    private readonly CilTokenizer _tokenizer;
    private long _version = -1;
    private int _start;
    private int _end;
    private bool _commentOpen;
    private IReadOnlyList<TextDecorationSpan> _cached = [];

    /// <summary>
    /// Initializes a provider over a tokenizer.
    /// </summary>
    /// <param name="tokenizer">The tokenizer.</param>
    public CilDecorationProvider(CilTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        _tokenizer = tokenizer;
    }

    /// <summary>
    /// Whether the engine has a <c>/*</c> open when the buffer starts.
    /// </summary>
    public bool CommentOpenAtStart { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<TextDecorationSpan> GetDecorations(int startLine, int endLine, IHex1bDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version == _version && startLine == _start && endLine == _end && CommentOpenAtStart == _commentOpen)
        {
            return _cached;
        }

        var inComment = CommentOpenAtStart;
        for (var line = 1; line < startLine && line <= document.LineCount; line++)
        {
            _tokenizer.Tokenize(document.GetLineText(line), ref inComment);
        }

        var spans = new List<TextDecorationSpan>();
        var last = Math.Min(endLine, document.LineCount);
        for (var line = Math.Max(1, startLine); line <= last; line++)
        {
            foreach (var token in _tokenizer.Tokenize(document.GetLineText(line), ref inComment))
            {
                var decoration = SpanPalette.Decoration(token.Style);
                if (decoration is not null)
                {
                    spans.Add(new TextDecorationSpan(new DocumentPosition(line, token.Start + 1), new DocumentPosition(line, token.End + 1), decoration));
                }
            }
        }

        (_version, _start, _end, _commentOpen, _cached) = (document.Version, startLine, endLine, CommentOpenAtStart, spans);
        return spans;
    }
}
