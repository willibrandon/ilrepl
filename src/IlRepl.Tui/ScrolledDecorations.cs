using System.Globalization;
using Hex1b.Documents;

namespace IlRepl.Tui;

/// <summary>
/// The decorations of a line scrolled sideways, cut so that none begins on a surrogate pair at
/// the scrolled edge: a style that starts inside a character built from several code units
/// would split that character on screen, so such a style starts at the next character that is
/// a whole code unit instead, and the wide characters before it wear the plain colour.
/// </summary>
public sealed class ScrolledDecorations : ITextDecorationProvider
{
    private readonly IReadOnlyList<ITextDecorationProvider> _inner;
    private readonly int _left;

    /// <summary>
    /// Initializes the cut over the given providers.
    /// </summary>
    /// <param name="inner">The providers whose decorations are cut.</param>
    /// <param name="left">The character index the visible text starts at on every line.</param>
    public ScrolledDecorations(IReadOnlyList<ITextDecorationProvider> inner, int left)
    {
        _inner = inner;
        _left = left;
    }

    /// <inheritdoc />
    public IReadOnlyList<TextDecorationSpan> GetDecorations(int startLine, int endLine, IHex1bDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var spans = new List<TextDecorationSpan>();
        foreach (var provider in _inner)
        {
            foreach (var span in provider.GetDecorations(startLine, endLine, document))
            {
                if (_left == 0 || span.Start.Line != span.End.Line || span.Start.Column - 1 > _left)
                {
                    spans.Add(span);
                    continue;
                }

                var line = document.GetLineText(span.Start.Line);
                var safe = SafeStart(line, _left);
                if (safe < span.End.Column - 1)
                {
                    spans.Add(span with { Start = new DocumentPosition(span.Start.Line, safe + 1) });
                }
            }
        }

        return spans;
    }

    /// <summary>
    /// The first character index at or after the scrolled edge where a style may start: the
    /// start of a text element that is not a surrogate pair.
    /// </summary>
    /// <param name="line">The line's text.</param>
    /// <param name="left">The character index the visible text starts at.</param>
    /// <returns>The index, or the line's length when no such character follows.</returns>
    public static int SafeStart(string line, int left)
    {
        ArgumentNullException.ThrowIfNull(line);
        var index = 0;
        while (index < line.Length)
        {
            var length = Math.Max(1, StringInfo.GetNextTextElementLength(line.AsSpan(index)));
            if (index + length > left && !char.IsSurrogate(line[index]))
            {
                return Math.Max(index, Math.Min(left, line.Length));
            }

            index += length;
        }

        return line.Length;
    }
}
