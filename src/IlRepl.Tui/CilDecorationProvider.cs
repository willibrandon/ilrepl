using Hex1b.Documents;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Colors the buffer and current-document diagnostics while preserving lexical state across viewport boundaries.
/// </summary>
public sealed class CilDecorationProvider : ITextDecorationProvider
{
    private readonly CilTokenizer _tokenizer;
    private IHex1bDocument? _prefixDocument;
    private long _prefixVersion = -1;
    private bool _prefixCommentOpen;
    private readonly List<bool> _commentStarts = [];
    private long _version = -1;
    private int _start;
    private int _end;
    private bool _commentOpen;
    private DocumentPosition? _caret;
    private IReadOnlyList<TextDecorationSpan> _cached = [];
    private IReadOnlyList<AnalysisDiagnostic> _diagnostics = [];
    private IEditorSession? _session;
    private bool _commentOpenAtStart;

    /// <summary>
    /// Source diagnostics matching the current document revision.
    /// </summary>
    public IReadOnlyList<AnalysisDiagnostic> Diagnostics
    {
        get => _diagnostics;
        set
        {
            if (!ReferenceEquals(_diagnostics, value) && !_diagnostics.SequenceEqual(value))
            {
                _diagnostics = value;
                _version = -1;
                _session?.Invalidate();
            }
        }
    }

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
    public bool CommentOpenAtStart
    {
        get => _commentOpenAtStart;
        set
        {
            if (_commentOpenAtStart == value) return;
            _commentOpenAtStart = value;
            _session?.Invalidate();
        }
    }

    /// <summary>
    /// Retains the editor's invalidation hook for decorations changed without a document edit.
    /// </summary>
    public void Activate(IEditorSession session) => _session = session;

    /// <summary>
    /// Releases the editor's invalidation hook when this provider is detached.
    /// </summary>
    public void Deactivate() => _session = null;

    /// <summary>
    /// Where the caret is. A first word the caret is still at the end of is not marked wrong
    /// while it can still become an opcode, a directive, or a command; it is marked once it
    /// cannot, or once the caret has left it.
    /// </summary>
    public DocumentPosition? Caret { get; set; }

    /// <summary>
    /// Colors source tokens and underlines errors belonging to this document.
    /// </summary>
    public IReadOnlyList<TextDecorationSpan> GetDecorations(int startLine, int endLine, IHex1bDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!ReferenceEquals(document, _prefixDocument) || document.Version != _prefixVersion
            || CommentOpenAtStart != _prefixCommentOpen)
        {
            _prefixDocument = document;
            _prefixVersion = document.Version;
            _prefixCommentOpen = CommentOpenAtStart;
            _commentStarts.Clear();
            _commentStarts.Add(CommentOpenAtStart);
            _version = -1;
        }
        if (document.Version == _version && startLine == _start && endLine == _end && CommentOpenAtStart == _commentOpen && Caret == _caret)
        {
            return _cached;
        }

        // PromptView requests each visible row separately. Scan preceding lexical state only
        // once per document revision, rather than repeating the whole prefix for every row.
        var first = Math.Clamp(startLine, 1, document.LineCount + 1);
        while (_commentStarts.Count < first)
        {
            var open = _commentStarts[^1];
            _tokenizer.Tokenize(document.GetLineText(_commentStarts.Count), ref open);
            _commentStarts.Add(open);
        }
        var inComment = _commentStarts[first - 1];

        var spans = new List<TextDecorationSpan>();
        var last = Math.Min(endLine, document.LineCount);
        for (var line = Math.Max(1, startLine); line <= last; line++)
        {
            var text = document.GetLineText(line);
            foreach (var token in _tokenizer.Tokenize(text, ref inComment))
            {
                if (token.Style == SpanStyle.Error && StillTyping(line, token, text))
                {
                    continue;
                }

                var decoration = SpanPalette.Decoration(token.Style);
                if (decoration is not null)
                {
                    spans.Add(new TextDecorationSpan(new DocumentPosition(line, token.Start + 1), new DocumentPosition(line, token.End + 1), decoration));
                }
            }
        }

        foreach (var diagnostic in Diagnostics.Where(d => d.Kind == AnalysisDiagnosticKind.Error
            && d.Explanation?.Source is null or { Kind: AnalysisSourceKind.Document }))
        {
            var location = diagnostic.Location;
            var line = location.Line + 1;
            if (line < Math.Max(1, startLine) || line > last)
            {
                continue;
            }

            var length = document.GetLineText(line).Length;
            var start = Math.Clamp(location.Start, 0, length);
            var end = Math.Clamp(location.Start + location.Length, start, length);
            if (end > start)
            {
                spans.Add(new TextDecorationSpan(new DocumentPosition(line, start + 1), new DocumentPosition(line, end + 1),
                    SpanPalette.Decoration(SpanStyle.Error)!));
            }
        }

        (_version, _start, _end, _commentOpen, _caret, _cached) = (document.Version, startLine, endLine, CommentOpenAtStart, Caret, spans);
        return spans;
    }

    // The word under the caret, with the caret at its end, is still being typed: it is wrong
    // only once nothing in the vocabulary begins with it.
    private bool StillTyping(int line, CilToken token, string text)
    {
        if (Caret is not { } caret || caret.Line != line || caret.Column - 1 != token.End)
        {
            return false;
        }

        var word = text.AsSpan(token.Start, token.Length);
        var vocabulary = _tokenizer.Vocabulary;
        return Begins(vocabulary.Opcodes.Keys, word) || Begins(vocabulary.Directives, word) || Begins(vocabulary.Commands, word);
    }

    private static bool Begins(IEnumerable<string> names, ReadOnlySpan<char> word)
    {
        foreach (var name in names)
        {
            if (name.Length > word.Length && name.AsSpan().StartsWith(word, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
