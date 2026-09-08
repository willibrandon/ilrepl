using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Tests for <see cref="SubmissionSplitter"/>: which lines make a unit and which are sent.
/// </summary>
[TestClass]
public sealed class SubmissionSplitterTests
{
    private static SubmissionUnit Unit(int start, int end, SubmissionUnitKind kind, params int[] sends) => new(start, end, sends, kind);

    /// <summary>
    /// Top-level lines are units of their own; a blank line is a run; a comment line is a line.
    /// </summary>
    [TestMethod]
    public void Split_TopLevel_OneUnitPerLine()
    {
        var units = SubmissionSplitter.Split(["ldc.i4.1", "", "ldc.i4.2", "// note", ""]);
        Assert.AreSequenceEqual(
        [
            Unit(0, 1, SubmissionUnitKind.Line, 0),
            Unit(1, 2, SubmissionUnitKind.Run, 1),
            Unit(2, 3, SubmissionUnitKind.Line, 2),
            Unit(3, 4, SubmissionUnitKind.Line, 3),
            Unit(4, 5, SubmissionUnitKind.Run, 4),
        ], units);
    }

    /// <summary>
    /// A method is one unit from its header to its closing brace, blank lines inside it not sent.
    /// </summary>
    [TestMethod]
    public void Split_Method_IsOneBlock()
    {
        var units = SubmissionSplitter.Split([".method int32 F() {", "  ldc.i4 1", "", "  ret", "}", "call int32 F()"]);
        Assert.AreSequenceEqual(
        [
            Unit(0, 5, SubmissionUnitKind.Block, 0, 1, 3, 4),
            Unit(5, 6, SubmissionUnitKind.Line, 5),
        ], units);
    }

    /// <summary>
    /// A protected region with its handlers is one unit, because the handler headers keep the depth above zero.
    /// </summary>
    [TestMethod]
    public void Split_TryChain_IsOneBlock()
    {
        var lines = new[] { ".try {", "nop", "leave END", "} catch [System.Runtime]System.Exception {", "pop", "leave END", "} finally {", "nop", "}", "END: nop" };
        var units = SubmissionSplitter.Split(lines);
        Assert.HasCount(2, units);
        Assert.AreEqual(Unit(0, 9, SubmissionUnitKind.Block, 0, 1, 2, 3, 4, 5, 6, 7, 8), units[0]);
        Assert.AreEqual(Unit(9, 10, SubmissionUnitKind.Line, 9), units[1]);
    }

    /// <summary>
    /// A comment inside a block is sent and never ends the block; a blank line inside a block
    /// comment is a comment too.
    /// </summary>
    [TestMethod]
    public void Split_CommentsInsideBlock_SentNotBoundaries()
    {
        var units = SubmissionSplitter.Split([".method int32 F() {", "  // body", "  /* open", "", "  } */ ldc.i4 1", "  ret", "}"]);
        Assert.AreSequenceEqual([Unit(0, 7, SubmissionUnitKind.Block, 0, 1, 2, 3, 4, 5, 6)], units);
    }

    /// <summary>
    /// A class with a method inside is one unit to the outermost brace.
    /// </summary>
    [TestMethod]
    public void Split_NestedClass_IsOneBlock()
    {
        var units = SubmissionSplitter.Split([".class public C {", "  .method public static int32 One() {", "    ldc.i4 1", "    ret", "  }", "}", "call int32 C::One()"]);
        Assert.HasCount(2, units);
        Assert.AreEqual(Unit(0, 6, SubmissionUnitKind.Block, 0, 1, 2, 3, 4, 5), units[0]);
    }

    /// <summary>
    /// When the engine already has a region open, the buffer's first lines belong to a unit that ends where the depth returns to zero.
    /// </summary>
    [TestMethod]
    public void Split_EngineOpenDepth_ContinuesTheRegion()
    {
        var units = SubmissionSplitter.Split(["nop", "", "}", "ldc.i4 1"], openDepth: 1);
        Assert.AreSequenceEqual(
        [
            Unit(0, 3, SubmissionUnitKind.Block, 0, 2),
            Unit(3, 4, SubmissionUnitKind.Line, 3),
        ], units);
    }

    /// <summary>
    /// When the engine has a comment open, the lines until it closes are comment lines.
    /// </summary>
    [TestMethod]
    public void Split_EngineOpenComment_LinesAreComments()
    {
        var units = SubmissionSplitter.Split(["still", "", "done */ ldc.i4 1"], inBlockComment: true);
        Assert.AreSequenceEqual(
        [
            Unit(0, 1, SubmissionUnitKind.Line, 0),
            Unit(1, 2, SubmissionUnitKind.Line, 1),
            Unit(2, 3, SubmissionUnitKind.Line, 2),
        ], units);
    }

    /// <summary>
    /// A closing brace with nothing open is a line of its own, for the engine to refuse.
    /// </summary>
    [TestMethod]
    public void Split_ExtraClose_IsALine()
    {
        var units = SubmissionSplitter.Split(["}", "nop"]);
        Assert.AreSequenceEqual([Unit(0, 1, SubmissionUnitKind.Line, 0), Unit(1, 2, SubmissionUnitKind.Line, 1)], units);
    }

    /// <summary>
    /// A block left open runs to the end of the buffer.
    /// </summary>
    [TestMethod]
    public void Split_UnclosedBlock_RunsToTheEnd()
    {
        var units = SubmissionSplitter.Split([".method int32 F() {", "ret"]);
        Assert.AreSequenceEqual([Unit(0, 2, SubmissionUnitKind.Block, 0, 1)], units);
    }

    /// <summary>
    /// The exact two-cell payload: each blank line at the top level runs the cell.
    /// </summary>
    [TestMethod]
    public void Split_TwoCellsSeparatedByBlanks()
    {
        var units = SubmissionSplitter.Split(PastePayload.Prepare("ldc.i4.1\n\nldc.i4.2\n\n").Split('\n'));
        Assert.AreSequenceEqual(
        [
            Unit(0, 1, SubmissionUnitKind.Line, 0),
            Unit(1, 2, SubmissionUnitKind.Run, 1),
            Unit(2, 3, SubmissionUnitKind.Line, 2),
            Unit(3, 4, SubmissionUnitKind.Run, 3),
        ], units);
    }

    /// <summary>
    /// A brace inside a string opens nothing.
    /// </summary>
    [TestMethod]
    public void Split_BraceInString_IsNotABlock()
    {
        var units = SubmissionSplitter.Split(["ldstr \"{\"", "ret"]);
        Assert.AreSequenceEqual([Unit(0, 1, SubmissionUnitKind.Line, 0), Unit(1, 2, SubmissionUnitKind.Line, 1)], units);
    }

    /// <summary>
    /// A header with its brace on the next line is one block with the lines the brace encloses.
    /// </summary>
    [TestMethod]
    public void Split_HeaderWithBraceOnNextLine_IsOneBlock()
    {
        var units = SubmissionSplitter.Split([".method int32 One()", "{", "  ldc.i4 1", "  ret", "}", "nop"]);
        Assert.AreSequenceEqual(
            [new SubmissionUnit(0, 5, [0, 1, 2, 3, 4], SubmissionUnitKind.Block), new SubmissionUnit(5, 6, [5], SubmissionUnitKind.Line)],
            units);
    }

    /// <summary>
    /// A region whose braces sit on their own lines, or are left out, is still one block.
    /// </summary>
    [TestMethod]
    public void Split_BracelessRegion_IsOneBlock()
    {
        var units = SubmissionSplitter.Split([".try", "{", "  nop", "} catch [System.Runtime]System.Exception", "{", "  pop", "}", "nop"]);
        Assert.AreSequenceEqual(
            [new SubmissionUnit(0, 7, [0, 1, 2, 3, 4, 5, 6], SubmissionUnitKind.Block), new SubmissionUnit(7, 8, [7], SubmissionUnitKind.Line)],
            units);
        var keywords = SubmissionSplitter.Split([".try {", "  nop", "catch [System.Runtime]System.Exception {", "  pop", "}"]);
        Assert.HasCount(1, keywords);
        Assert.AreEqual(SubmissionUnitKind.Block, keywords[0].Kind);
    }

    /// <summary>
    /// A command whose argument holds a brace is one line, not the start of a block.
    /// </summary>
    [TestMethod]
    public void Split_CommandWithABraceInItsArgument_IsOneLine()
    {
        var units = SubmissionSplitter.Split([".load /tmp/cell{draft.dll", "nop"], commands: [".load"]);
        Assert.AreSequenceEqual(
            [new SubmissionUnit(0, 1, [0], SubmissionUnitKind.Line), new SubmissionUnit(1, 2, [1], SubmissionUnitKind.Line)],
            units);
    }

    /// <summary>
    /// A comment between the closing brace and the handler keyword does not end the block early.
    /// </summary>
    [TestMethod]
    public void Split_HandlerPartedByAComment_IsOneBlock()
    {
        var units = SubmissionSplitter.Split([".try {", "  nop", "} /* note */ catch [System.Runtime]System.Exception {", "  pop", "}", "nop"]);
        Assert.AreSequenceEqual(
            [new SubmissionUnit(0, 5, [0, 1, 2, 3, 4], SubmissionUnitKind.Block), new SubmissionUnit(5, 6, [5], SubmissionUnitKind.Line)],
            units);
    }
}
