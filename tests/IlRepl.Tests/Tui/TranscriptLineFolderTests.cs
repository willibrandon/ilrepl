using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Folds real transcript lines, the help text among them, and checks the rows that come out.
/// </summary>
[TestClass]
public sealed class TranscriptLineFolderTests
{
    /// <summary>
    /// A line that fits is returned as one row with its runs untouched.
    /// </summary>
    [TestMethod]
    public void ShortLine_IsOneRow()
    {
        var line = HelpText.Lines().First(l => Text(l) == "  ldc.i4 6");
        var rows = TranscriptLineFolder.Fold(line.Spans, 80);
        Assert.HasCount(1, rows);
        Assert.AreEqual(Text(line), Join(rows[0]));
    }

    /// <summary>
    /// A word at the width boundary consumes its separator without losing a later word or an earlier fitting word.
    /// </summary>
    /// <param name="text">The complete line.</param>
    /// <param name="width">The row width in terminal cells.</param>
    /// <param name="expected">The exact rows in source order.</param>
    [TestMethod]
    [DataRow("ab cd", 1, new[] { "a", "b", "c", "d" })]
    [DataRow("ab cd", 2, new[] { "ab", "cd" })]
    [DataRow("ab cd", 3, new[] { "ab", "cd" })]
    [DataRow("ab cd", 5, new[] { "ab cd" })]
    [DataRow("a bc de", 4, new[] { "a bc", "de" })]
    [DataRow("a  bc", 3, new[] { "a ", "bc" })]
    public void WordBoundary_UsesTheAvailableRow(string text, int width, string[] expected)
    {
        var rows = TranscriptLineFolder.Fold([new TranscriptSpan(text)], width);

        Assert.AreSequenceEqual(expected, rows.Select(Join));
    }

    /// <summary>
    /// Initial indentation survives folding even when it fills a row or makes up the entire line.
    /// </summary>
    /// <param name="text">The indented line.</param>
    /// <param name="width">The row width in terminal cells.</param>
    /// <param name="expected">The exact rows including every indentation space.</param>
    [TestMethod]
    [DataRow("  ab cd", 2, new[] { "  ", "ab", "cd" })]
    [DataRow("  ab cd", 4, new[] { "  ab", "cd" })]
    [DataRow("    ", 2, new[] { "  ", "  " })]
    [DataRow("   ab", 2, new[] { "  ", " a", "b" })]
    public void LeadingSpaces_AreNeverDropped(string text, int width, string[] expected)
    {
        var rows = TranscriptLineFolder.Fold([new TranscriptSpan(text)], width);

        Assert.AreSequenceEqual(expected, rows.Select(Join));
    }

    /// <summary>
    /// Removing a boundary separator preserves the styles on both sides of a run boundary.
    /// </summary>
    [TestMethod]
    public void WordBoundary_PreservesRunStyles()
    {
        var rows = TranscriptLineFolder.Fold(
            [new TranscriptSpan("ab", SpanStyle.Opcode), new TranscriptSpan(" cd", SpanStyle.Type)], 2);

        Assert.HasCount(2, rows);
        Assert.AreSequenceEqual([new TranscriptSpan("ab", SpanStyle.Opcode)], rows[0]);
        Assert.AreSequenceEqual([new TranscriptSpan("cd", SpanStyle.Type)], rows[1]);
    }

    /// <summary>
    /// Wide, joined, and combining text elements remain indivisible even when wider than the requested row.
    /// </summary>
    /// <param name="width">The row width in terminal cells.</param>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void UnicodeElements_RemainWhole(int width)
    {
        var rows = TranscriptLineFolder.Fold([new TranscriptSpan("界 👩‍💻 é", SpanStyle.Type)], width);

        Assert.AreSequenceEqual(["界", "👩‍💻", "é"], rows.Select(Join));
        foreach (var row in rows)
        {
            Assert.AreEqual(SpanStyle.Type, Assert.ContainsSingle(row).Style);
        }
    }

    /// <summary>
    /// A paragraph breaks at spaces, fills each row as far as it can, and drops only the space
    /// at the break.
    /// </summary>
    [TestMethod]
    public void Paragraph_BreaksAtSpacesAndFillsRows()
    {
        var line = HelpText.Lines().First(l => Text(l).StartsWith("Type one IL instruction", StringComparison.Ordinal));
        var rows = TranscriptLineFolder.Fold(line.Spans, 59);
        Assert.AreEqual("Type one IL instruction per line. The simulated stack is", Join(rows[0]));
        Assert.AreEqual("shown after each one.", Join(rows[1]));
        Assert.HasCount(2, rows);
    }

    /// <summary>
    /// A label followed by a description folds the description under itself, at the label's width,
    /// and the label keeps its style.
    /// </summary>
    [TestMethod]
    public void LabeledLine_FoldsWithHangingIndent()
    {
        var line = new TranscriptLine(LineKind.Listing,
        [
            new TranscriptSpan("  .args (T name = literal, ...)  ", SpanStyle.Command),
            new TranscriptSpan("cell arguments and the values passed each run"),
        ]);
        var rows = TranscriptLineFolder.Fold(line.Spans, 59);
        Assert.HasCount(2, rows);
        Assert.AreEqual(SpanStyle.Command, rows[0][0].Style);
        Assert.StartsWith("  .args (T name = literal, ...)", Join(rows[0]));
        Assert.StartsWith(new string(' ', 32), Join(rows[1]));
        Assert.AreEqual("cell arguments and the values passed each run", (Join(rows[0])[32..] + " " + Join(rows[1]).TrimStart()).Trim());
    }

    /// <summary>
    /// Leading spaces are kept, and a word wider than the row is broken rather than lost.
    /// </summary>
    [TestMethod]
    public void Indentation_IsKeptAndLongWordsBreak()
    {
        var line = TranscriptLine.Of(LineKind.Listing, "  call int32 [System.Runtime]System.Math::Max(int32, int32)");
        var rows = TranscriptLineFolder.Fold(line.Spans, 30);
        Assert.StartsWith("  call int32", Join(rows[0]));
        Assert.AreEqual(Text(line).Replace(" ", "", StringComparison.Ordinal),
            string.Concat(rows.Select(r => Join(r).Replace(" ", "", StringComparison.Ordinal))), "every character should survive folding");
        Assert.DoesNotContain(r => Join(r).Length > 30, rows);
    }

    /// <summary>
    /// A width of zero means the size is not known yet, and the line is left whole.
    /// </summary>
    [TestMethod]
    public void UnknownWidth_LeavesLineWhole()
    {
        var line = HelpText.Lines().First(l => Text(l).StartsWith("Type one IL instruction", StringComparison.Ordinal));
        Assert.HasCount(1, TranscriptLineFolder.Fold(line.Spans, 0));
    }

    private static string Text(TranscriptLine line) => string.Concat(line.Spans.Select(s => s.Text));

    private static string Join(IReadOnlyList<TranscriptSpan> row) => string.Concat(row.Select(s => s.Text));

    /// <summary>
    /// An echoed line folds under its input at the prompt's width, however many runs the
    /// tokenizer cut the input into.
    /// </summary>
    [TestMethod]
    public void EchoLine_FoldsUnderTheInput()
    {
        var core = new ReplCore();
        core.Handle("call int32 [System.Runtime]System.Math::Max(int32, int32)");
        var echo = core.Transcript.Lines[0];
        Assert.IsGreaterThan(3, echo.Spans.Count);
        var rows = TranscriptLineFolder.Fold(echo.Spans, 40);
        Assert.IsGreaterThanOrEqualTo(2, rows.Count);
        Assert.StartsWith("il[1]> call int32", Join(rows[0]));
        foreach (var row in rows.Skip(1))
        {
            Assert.StartsWith("       ", Join(row));
            Assert.AreNotEqual(' ', Join(row)[7]);
        }

        Assert.AreEqual(echo.PlainText.Replace(" ", "", StringComparison.Ordinal),
            string.Concat(rows.Select(Join)).Replace(" ", "", StringComparison.Ordinal));
    }
}
