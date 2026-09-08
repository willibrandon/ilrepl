using System.Text;
using IlRepl.Protocol;

namespace IlRepl.Batch;

/// <summary>
/// Writes transcript lines to a <see cref="TextWriter"/>, with ANSI colors when asked.
/// </summary>
public static class AnsiWriter
{
    /// <summary>
    /// Renders a line as text, with ANSI escapes when <paramref name="color"/> is true.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="color">Whether to emit colors.</param>
    /// <returns>The rendered line without a trailing newline.</returns>
    public static string Render(TranscriptLine line, bool color)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!color)
        {
            return line.PlainText;
        }

        var sb = new StringBuilder();
        foreach (var span in line.Spans)
        {
            var code = Code(span.Style);
            if (code.Length == 0)
            {
                sb.Append(span.Text);
            }
            else
            {
                sb.Append("\x1b[").Append(code).Append('m').Append(span.Text).Append("\x1b[0m");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Writes a line followed by a newline.
    /// </summary>
    /// <param name="writer">The destination.</param>
    /// <param name="line">The line.</param>
    /// <param name="color">Whether to emit colors.</param>
    public static void Write(TextWriter writer, TranscriptLine line, bool color)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(Render(line, color));
    }

    private static string Code(SpanStyle style) => style switch
    {
        SpanStyle.Dim => "2",
        SpanStyle.Prompt => "1;34",
        SpanStyle.Opcode => "36",
        SpanStyle.Type => "36",
        SpanStyle.TopType => "1;36",
        SpanStyle.Label => "33",
        SpanStyle.Number => "33",
        SpanStyle.String => "32",
        SpanStyle.Keyword => "35",
        SpanStyle.Error => "31",
        SpanStyle.Command => "34",
        SpanStyle.Heading => "1",
        SpanStyle.Directive => "34",
        SpanStyle.Member => "34",
        SpanStyle.Comment => "2",
        SpanStyle.Punctuation => "2",
        _ => "",
    };
}
