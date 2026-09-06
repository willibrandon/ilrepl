using System.Collections;
using System.Globalization;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Formats the value a cell returned as styled spans: strings quoted, numbers as numbers,
/// arrays and collections expanded, everything else through <c>ToString</c>.
/// </summary>
public static class ValueFormatter
{
    private const int MaxItems = 32;

    /// <summary>
    /// Formats a value followed by its runtime type.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The spans.</returns>
    public static IReadOnlyList<TranscriptSpan> FormatWithType(object? value)
    {
        var spans = new List<TranscriptSpan>();
        Append(spans, value, 0);
        if (value is not null)
        {
            spans.Add(new TranscriptSpan(" : " + TypeNameFormatter.Pretty(value.GetType()), SpanStyle.Dim));
        }

        return spans;
    }

    /// <summary>
    /// Formats a value without its type.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The spans.</returns>
    public static IReadOnlyList<TranscriptSpan> Format(object? value)
    {
        var spans = new List<TranscriptSpan>();
        Append(spans, value, 0);
        return spans;
    }

    private static void Append(List<TranscriptSpan> spans, object? value, int depth)
    {
        switch (value)
        {
            case null:
                spans.Add(new TranscriptSpan("null", SpanStyle.Keyword));
                return;
            case string s:
                spans.Add(new TranscriptSpan(LiteralParser.Escape(s), SpanStyle.String));
                return;
            case char c:
                spans.Add(new TranscriptSpan("'" + (c == '\'' ? "\\'" : c.ToString()) + "'", SpanStyle.String));
                return;
            case bool b:
                spans.Add(new TranscriptSpan(b ? "true" : "false", SpanStyle.Keyword));
                return;
            case IFormattable f when IsNumeric(value):
                spans.Add(new TranscriptSpan(f.ToString(null, CultureInfo.InvariantCulture), SpanStyle.Number));
                return;
            case Type t:
                spans.Add(new TranscriptSpan("typeof(" + TypeNameFormatter.Pretty(t) + ")", SpanStyle.Type));
                return;
            case Array array when array.Rank == 1 && depth < 2:
                AppendSequence(spans, array, "[", "]", depth);
                return;
            case IEnumerable enumerable when depth < 2 && value is not string:
                AppendSequence(spans, enumerable, "{", "}", depth);
                return;
            default:
                break;
        }

        var text = value.ToString() ?? "";
        var type = value.GetType();
        if (text == type.FullName || text == type.ToString())
        {
            spans.Add(new TranscriptSpan("{" + TypeNameFormatter.Pretty(type) + "}", SpanStyle.Dim));
        }
        else
        {
            spans.Add(new TranscriptSpan(text));
        }
    }

    private static void AppendSequence(List<TranscriptSpan> spans, IEnumerable items, string open, string close, int depth)
    {
        spans.Add(new TranscriptSpan(open));
        var count = 0;
        foreach (var item in items)
        {
            if (count > 0)
            {
                spans.Add(new TranscriptSpan(", "));
            }

            if (count++ >= MaxItems)
            {
                spans.Add(new TranscriptSpan("…", SpanStyle.Dim));
                break;
            }

            Append(spans, item, depth + 1);
        }

        spans.Add(new TranscriptSpan(close));
    }

    private static bool IsNumeric(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal or nint or nuint;
}
