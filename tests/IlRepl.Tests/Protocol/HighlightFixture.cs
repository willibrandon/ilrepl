using System.Text.RegularExpressions;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Reads a highlight fixture in the shape tree-sitter uses: a subject line, then annotation
/// lines whose carets sit under the subject's columns. <c>// &lt;- style</c> marks the token at
/// column 0 and <c>//   ^^^ style</c> marks the token that starts under the first caret and covers
/// the run. Any other line is a subject; a blank subject must produce no tokens. Comment state
/// carries through the file in order.
/// </summary>
internal static partial class HighlightFixture
{
    [GeneratedRegex(@"^\s*//\s*(<-|\^+)\s+([A-Za-z]+)\s*$")]
    private static partial Regex Annotation();

    /// <summary>
    /// Parses a fixture file.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The cases, in order.</returns>
    public static IReadOnlyList<HighlightCase> Parse(string path)
    {
        var lines = File.ReadAllLines(path);
        var cases = new List<HighlightCase>();
        string? subject = null;
        var subjectLine = 0;
        var expectations = new List<HighlightExpectation>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = Annotation().Match(line);
            if (match.Success && subject is not null && Enum.TryParse<SpanStyle>(match.Groups[2].Value, ignoreCase: true, out var style))
            {
                var mark = match.Groups[1].Value;
                if (mark == "<-")
                {
                    expectations.Add(new HighlightExpectation(i + 1, 0, 0, style));
                }
                else
                {
                    var column = line.IndexOf('^', StringComparison.Ordinal);
                    expectations.Add(new HighlightExpectation(i + 1, column, mark.Length, style));
                }

                continue;
            }

            if (subject is not null)
            {
                cases.Add(new HighlightCase(subjectLine, subject, expectations));
                expectations = [];
            }

            subject = line;
            subjectLine = i + 1;
        }

        if (subject is not null)
        {
            cases.Add(new HighlightCase(subjectLine, subject, expectations));
        }

        return cases;
    }
}
