using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="LiteralParser"/>.
/// </summary>
[TestClass]
public sealed class LiteralParserTests
{
    /// <summary>
    /// Integer literals in every accepted form.
    /// </summary>
    /// <param name="text">The literal.</param>
    /// <param name="expected">The value.</param>
    [TestMethod]
    [DataRow("42", 42L)]
    [DataRow("-7", -7L)]
    [DataRow("0x1F", 31L)]
    [DataRow("0b101", 5L)]
    [DataRow("'A'", 65L)]
    [DataRow("1_000", 1000L)]
    [DataRow("0xFFFFFFFFFFFFFFFF", -1L)]
    public void ParseInteger_AcceptedForms_ReturnValue(string text, long expected)
    {
        Assert.AreEqual(expected, LiteralParser.ParseInteger(text, "test"));
    }

    /// <summary>
    /// Floating-point literals, including the ILAsm bit-pattern forms.
    /// </summary>
    [TestMethod]
    public void ParseFloat_AcceptedForms_ReturnValue()
    {
        Assert.AreEqual(2.5, LiteralParser.ParseFloat("2.5", "test"));
        Assert.AreEqual(1.0, LiteralParser.ParseFloat("float64(0x3FF0000000000000)", "test"));
        Assert.AreEqual(1.0, LiteralParser.ParseFloat("float32(0x3F800000)", "test"));
        Assert.IsTrue(double.IsNaN(LiteralParser.ParseFloat("nan", "test")));
        Assert.IsTrue(double.IsPositiveInfinity(LiteralParser.ParseFloat("inf", "test")));
    }

    /// <summary>
    /// String escapes and the bytearray form decode correctly.
    /// </summary>
    [TestMethod]
    public void ParseString_EscapesAndByteArray_Decode()
    {
        Assert.AreEqual("a\tb\n\"c\"", LiteralParser.ParseString("\"a\\tb\\n\\\"c\\\"\""));
        Assert.AreEqual("A", LiteralParser.ParseString("\"\\u0041\""));
        Assert.AreEqual("Hi", LiteralParser.ParseString("bytearray (48 00 69 00)"));
        Assert.AreEqual("bare", LiteralParser.ParseString("bare"));
    }

    /// <summary>
    /// Escaping round-trips through parsing.
    /// </summary>
    [TestMethod]
    public void Escape_RoundTrips()
    {
        const string original = "line1\nline2\t\"quoted\"\\";
        Assert.AreEqual(original, LiteralParser.ParseString(LiteralParser.Escape(original)));
    }

    /// <summary>
    /// Bad literals are reported as REPL errors.
    /// </summary>
    [TestMethod]
    public void ParseInteger_Invalid_ThrowsReplException()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => LiteralParser.ParseInteger("abc", "ldc.i4"));
        Assert.Contains("ldc.i4", ex.Message);
    }
}
