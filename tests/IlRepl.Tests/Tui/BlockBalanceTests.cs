using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Tests for <see cref="BlockBalance"/>: when Enter submits and when it continues.
/// </summary>
[TestClass]
public sealed class BlockBalanceTests
{
    /// <summary>
    /// A plain instruction is complete; a header with an open brace is not; a close alone is.
    /// </summary>
    [TestMethod]
    public void IsComplete_FollowsTheBraces()
    {
        Assert.IsTrue(BlockBalance.IsComplete("ldc.i4 1"));
        Assert.AreEqual(0, BlockBalance.Scan("ldc.i4 1").Depth);
        Assert.IsFalse(BlockBalance.IsComplete(".method int32 F() {"));
        Assert.IsFalse(BlockBalance.IsComplete(".method int32 F() {\nldc.i4 1\nret"));
        Assert.IsTrue(BlockBalance.IsComplete(".method int32 F() {\nldc.i4 1\nret\n}"));
        Assert.IsTrue(BlockBalance.IsComplete("}"));
        Assert.AreEqual(-1, BlockBalance.Scan("}").Depth);
        Assert.IsTrue(BlockBalance.IsComplete("}}"));
    }

    /// <summary>
    /// A catch header closes one brace and opens another, so the region stays open.
    /// </summary>
    [TestMethod]
    public void Scan_CatchHeader_KeepsDepth()
    {
        Assert.AreEqual(1, BlockBalance.Scan(".try {\nnop\n} catch [System.Runtime]System.Exception {").Depth);
        Assert.AreEqual(1, BlockBalance.Scan(".try {\nnop\n} finally {\nnop").Depth);
        Assert.AreEqual(0, BlockBalance.Scan(".try {\nnop\n} finally {\nnop\n}").Depth);
    }

    /// <summary>
    /// Braces inside strings, quoted names, character literals, and comments count for nothing.
    /// </summary>
    [TestMethod]
    public void Scan_IgnoresBracesInsideLiteralsAndComments()
    {
        Assert.IsTrue(BlockBalance.IsComplete("ldstr \"{\""));
        Assert.IsTrue(BlockBalance.IsComplete("ldstr \"\\\"{\""));
        Assert.IsTrue(BlockBalance.IsComplete("ldc.i4 '{'"));
        Assert.IsTrue(BlockBalance.IsComplete("ldarg '{ }'"));
        Assert.IsTrue(BlockBalance.IsComplete("nop // {"));
        Assert.IsTrue(BlockBalance.IsComplete("nop /* { */"));
        Assert.IsTrue(BlockBalance.IsComplete("/* {\n } */ nop"));
        Assert.AreEqual(0, BlockBalance.Scan("/* {\n{\n } */ nop").Depth);
    }

    /// <summary>
    /// An open comment or an open string on the last line keeps the buffer incomplete.
    /// </summary>
    [TestMethod]
    public void IsComplete_OpenCommentOrString_IsFalse()
    {
        var comment = BlockBalance.Scan("nop /* open");
        Assert.IsTrue(comment.InBlockComment);
        Assert.IsFalse(BlockBalance.IsComplete("nop /* open"));
        Assert.IsTrue(BlockBalance.IsComplete("nop /* open\nstill */"));

        var text = BlockBalance.Scan("ldstr \"abc");
        Assert.IsTrue(text.InString);
        Assert.IsFalse(BlockBalance.IsComplete("ldstr \"abc"));
        Assert.IsFalse(BlockBalance.IsComplete("ldarg 'abc"));
        Assert.IsTrue(BlockBalance.IsComplete("ldstr \"a\\\"b\""));
    }

    /// <summary>
    /// A string ends at its line, so a brace on the next line counts.
    /// </summary>
    [TestMethod]
    public void Scan_UnterminatedStringEndsAtLine_NextLineCounts()
    {
        var scan = BlockBalance.Scan("ldstr \"abc\n.try {");
        Assert.AreEqual(1, scan.Depth);
        Assert.IsFalse(scan.InString);
    }

    /// <summary>
    /// Windows line endings scan the same as Unix ones.
    /// </summary>
    [TestMethod]
    public void IsComplete_CrlfText_SameAsLf()
    {
        Assert.AreEqual(BlockBalance.Scan(".method int32 F() {\nret\n}"), BlockBalance.Scan(".method int32 F() {\r\nret\r\n}"));
        Assert.IsTrue(BlockBalance.IsComplete(".method int32 F() {\r\nret\r\n}"));
    }

    /// <summary>
    /// The scan starts from what the engine already has open: a region, or a comment.
    /// </summary>
    [TestMethod]
    public void Scan_StartsFromTheEngineState()
    {
        Assert.IsFalse(BlockBalance.IsComplete("nop", openDepth: 1));
        Assert.IsTrue(BlockBalance.IsComplete("}", openDepth: 1));
        Assert.IsTrue(BlockBalance.IsComplete("nop\n}", openDepth: 1));
        Assert.IsFalse(BlockBalance.IsComplete("nop", inBlockComment: true));
        Assert.IsTrue(BlockBalance.IsComplete("still */ nop", inBlockComment: true));
        Assert.IsTrue(BlockBalance.IsComplete("{ inside */ }", inBlockComment: true));
        Assert.AreEqual(2, BlockBalance.Scan(".try {", openDepth: 1).Depth);
    }

    /// <summary>
    /// An empty buffer is complete and has nothing open.
    /// </summary>
    [TestMethod]
    public void Scan_Empty_IsComplete()
    {
        Assert.AreEqual(new BlockScan(0, false, false), BlockBalance.Scan(""));
        Assert.IsTrue(BlockBalance.IsComplete(""));
    }

    /// <summary>
    /// A declaration header without its brace opens the block already, and the brace on the next
    /// line is the header's own rather than a deeper level, so the close brings the depth to zero.
    /// </summary>
    [TestMethod]
    public void Scan_HeaderWithoutBrace_WaitsForIt()
    {
        var header = BlockBalance.Scan(".method int32 One()");
        Assert.AreEqual(1, header.Depth);
        Assert.IsTrue(header.AwaitingBrace);
        Assert.IsFalse(BlockBalance.IsComplete(".method int32 One()"));

        var opened = BlockBalance.Scan(".method int32 One()\n{");
        Assert.AreEqual(1, opened.Depth);
        Assert.IsFalse(opened.AwaitingBrace);

        Assert.IsTrue(BlockBalance.IsComplete(".method int32 One()\n{\n  ret\n}"));
        Assert.IsTrue(BlockBalance.IsComplete(".class C\n{\n  .method public static void M()\n  {\n    ret\n  }\n}"));
        Assert.AreEqual(1, BlockBalance.Scan(".class C\n{\n  .method public static void M()\n  {\n    ret\n  }").Depth);

        // A header with its brace on the same line is unchanged, and a brace inherited from the
        // engine's own waiting header is consumed the same way.
        Assert.AreEqual(1, BlockBalance.Scan(".method int32 One() {").Depth);
        Assert.IsFalse(BlockBalance.Scan(".method int32 One() {").AwaitingBrace);
        Assert.AreEqual(1, BlockBalance.Scan("{", 1, awaitingBrace: true).Depth);
        Assert.IsTrue(BlockBalance.IsComplete("{\n  ret\n}", 1, awaitingBrace: true));
    }
}
