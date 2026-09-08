using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Tests for <see cref="CilLexer"/>: where strings, quoted names, and comments begin and end.
/// </summary>
[TestClass]
public sealed class CilLexerTests
{
    private static IReadOnlyList<CilSegment> Segments(string line)
    {
        var closed = false;
        return CilLexer.Segments(line, ref closed);
    }

    private static string Text(string line, CilSegment segment) => line.Substring(segment.Start, segment.Length);

    /// <summary>
    /// An escaped quote and a slash pair inside a string do not end the string or start a comment.
    /// </summary>
    [TestMethod]
    public void Segments_StringWithEscapes_IsOneSegment()
    {
        const string line = "ldstr \"a\\\"b//c\" // d";
        var segments = Segments(line);
        Assert.HasCount(4, segments);
        Assert.AreEqual(CilSegmentKind.Code, segments[0].Kind);
        Assert.AreEqual("\"a\\\"b//c\"", Text(line, segments[1]));
        Assert.AreEqual(CilSegmentKind.String, segments[1].Kind);
        Assert.AreEqual(CilSegmentKind.Code, segments[2].Kind);
        Assert.AreEqual("// d", Text(line, segments[3]));
        Assert.AreEqual(CilSegmentKind.LineComment, segments[3].Kind);
    }

    /// <summary>
    /// A quoted name may hold spaces, slashes, and escaped quotes; none of them ends it.
    /// </summary>
    [TestMethod]
    public void Segments_QuotedName_KeepsSlashesAndSpaces()
    {
        const string line = "ldarg 'a b//c\\'d'";
        var segments = Segments(line);
        Assert.HasCount(2, segments);
        Assert.AreEqual(CilSegmentKind.QuotedName, segments[1].Kind);
        Assert.AreEqual("'a b//c\\'d'", Text(line, segments[1]));
    }

    /// <summary>
    /// A block comment left open on one line continues on the next until it closes.
    /// </summary>
    [TestMethod]
    public void Segments_BlockCommentAcrossLines_CarriesState()
    {
        var open = false;
        var first = CilLexer.Segments("add /* open", ref open);
        Assert.IsTrue(open);
        Assert.HasCount(2, first);
        Assert.AreEqual(CilSegmentKind.BlockComment, first[1].Kind);

        var second = CilLexer.Segments("still */ sub", ref open);
        Assert.IsFalse(open);
        Assert.HasCount(2, second);
        Assert.AreEqual("still */", "still */ sub".Substring(second[0].Start, second[0].Length));
        Assert.AreEqual(CilSegmentKind.BlockComment, second[0].Kind);
        Assert.AreEqual(CilSegmentKind.Code, second[1].Kind);

        var third = CilLexer.Segments("", ref open);
        Assert.IsEmpty(third);
    }

    /// <summary>
    /// A blank line inside an open comment is one comment segment, and stays open.
    /// </summary>
    [TestMethod]
    public void Segments_InsideOpenComment_WholeLineIsComment()
    {
        var open = true;
        var segments = CilLexer.Segments("   ", ref open);
        Assert.IsTrue(open);
        Assert.HasCount(1, segments);
        Assert.AreEqual(CilSegmentKind.BlockComment, segments[0].Kind);
    }

    /// <summary>
    /// A string or quoted name with no closing quote runs to the end of the line.
    /// </summary>
    [TestMethod]
    public void Segments_Unterminated_RunToEndOfLine()
    {
        var segments = Segments("ldstr \"abc // not a comment");
        Assert.HasCount(2, segments);
        Assert.AreEqual(CilSegmentKind.String, segments[1].Kind);
        Assert.AreEqual("ldstr \"abc // not a comment".Length, segments[1].End);

        segments = Segments("ldarg 'abc");
        Assert.AreEqual(CilSegmentKind.QuotedName, segments[1].Kind);
        Assert.AreEqual("ldarg 'abc".Length, segments[1].End);
    }

    /// <summary>
    /// Segments start at zero, follow each other with no gaps, and end at the end of the line.
    /// </summary>
    /// <param name="line">The line.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("add")]
    [DataRow("ldc.i4.1// comment")]
    [DataRow("ldc.i4.2/* comment */")]
    [DataRow("call int32 [System.Runtime]System.Math::Max(int32, int32) // max")]
    [DataRow("ldstr \"a /* b */ c\" /* d */ 'e // f' // g")]
    [DataRow("/* a */ /* b */ nop /* c")]
    public void Segments_CoverTheLine(string line)
    {
        var segments = Segments(line);
        var next = 0;
        foreach (var segment in segments)
        {
            Assert.AreEqual(next, segment.Start);
            Assert.IsGreaterThan(0, segment.Length);
            next = segment.End;
        }

        Assert.AreEqual(line.Length, next);
    }

    /// <summary>
    /// Stripping removes comments and nothing else, so a comment between an opcode and its
    /// operand joins them, as ilasm reads it.
    /// </summary>
    [TestMethod]
    public void StripComments_RemovesOnlyComments()
    {
        var closed = false;
        Assert.AreEqual("ldc.i4 1", CilLexer.StripComments("ldc.i4 1// c", ref closed));
        Assert.AreEqual("ldc.i41", CilLexer.StripComments("ldc.i4/* x */1", ref closed));
        Assert.AreEqual("'a//b' ", CilLexer.StripComments("'a//b' // c", ref closed));
        Assert.AreEqual("ldstr \"http://x\" ", CilLexer.StripComments("ldstr \"http://x\" // c", ref closed));
        Assert.IsFalse(closed);
    }

    /// <summary>
    /// The state a line leaves is what the next line starts with.
    /// </summary>
    [TestMethod]
    public void StripComments_CarriesAnOpenComment()
    {
        var open = false;
        Assert.AreEqual("add ", CilLexer.StripComments("add /* open", ref open));
        Assert.IsTrue(open);
        Assert.AreEqual("", CilLexer.StripComments("still open", ref open));
        Assert.IsTrue(open);
        Assert.AreEqual(" sub", CilLexer.StripComments("done */ sub", ref open));
        Assert.IsFalse(open);
    }

    /// <summary>
    /// The end of a string is just past its closing quote; a backslash escapes the next character.
    /// </summary>
    [TestMethod]
    public void EndOfString_HonoursEscapes()
    {
        Assert.AreEqual(8, CilLexer.EndOfString("x \"a\\\"b\" y", 2));
        Assert.AreEqual(7, CilLexer.EndOfQuotedName("x 'a\\'' y", 2));
        Assert.AreEqual(4, CilLexer.EndOfString("x \"a", 2));
    }
}
