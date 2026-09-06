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
        var line = HelpText.Lines().First(l => Text(l) == "  ldc.i4 6                       .locals init (int32 i)");
        var rows = TranscriptLineFolder.Fold(line.Spans, 80);
        Assert.HasCount(1, rows);
        Assert.AreEqual(Text(line), Join(rows[0]));
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
        var line = HelpText.Lines().First(l => Text(l).StartsWith("  .args (T name = literal, ...)", StringComparison.Ordinal));
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
        Assert.AreEqual(Text(line).Replace(" ", "", StringComparison.Ordinal), string.Concat(rows.Select(r => Join(r).Replace(" ", "", StringComparison.Ordinal))), "every character should survive folding");
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
}
