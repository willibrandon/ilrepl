using System.Text.RegularExpressions;

namespace IlRepl.Protocol;

/// <summary>
/// Styles native instruction text without interpreting it as CIL or replaying it as a command.
/// </summary>
public static partial class NativeListingFormatter
{
    /// <summary>
    /// Splits a native listing line into terminal transcript spans without changing its text.
    /// </summary>
    /// <param name="line">An original or symbolized native line.</param>
    /// <returns>The styled, lossless transcript spans.</returns>
    public static IReadOnlyList<TranscriptSpan> Spans(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var spans = new List<TranscriptSpan>();
        var position = 0;
        var instruction = false;
        foreach (Match match in Tokens().Matches(line))
        {
            if (match.Index > position) spans.Add(new TranscriptSpan(line[position..match.Index]));
            var word = match.Value;
            var style = word.StartsWith(';') ? SpanStyle.Comment
                : word.StartsWith('"') ? SpanStyle.String
                : word.StartsWith('<') ? SpanStyle.Member
                : word.EndsWith(':') ? SpanStyle.Label
                : word.StartsWith('#') || char.IsDigit(word[0]) ? SpanStyle.Number
                : Register().IsMatch(word) ? SpanStyle.Type
                : !instruction && char.IsLetter(word[0]) ? SpanStyle.Opcode : SpanStyle.Default;
            if (style == SpanStyle.Opcode) instruction = true;
            spans.Add(new TranscriptSpan(word, style));
            position = match.Index + match.Length;
        }
        if (position < line.Length) spans.Add(new TranscriptSpan(line[position..]));
        return spans;
    }

    [GeneratedRegex(";.*|\"(?:[^\"\\\\]|\\\\.)*\"|<[^>]+>|[A-Za-z_][A-Za-z0-9_.]*(?::)?|#?-?(?:0[xX][0-9a-fA-F]+|[0-9]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    [GeneratedRegex(@"^(?:[xwqv]\d+(?:\.[0-9a-z]+)?|[re](?:ax|bx|cx|dx|si|di|bp|sp)|[xyz]mm\d+|r\d+(?:[dwb])?|sp|lr|fp)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Register();
}
