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

    /// <summary>
    /// float32 bit patterns are kept exactly, signaling NaNs included; every other spelling parses as a float.
    /// </summary>
    [TestMethod]
    public void ParseFloat32_KeepsBits()
    {
        Assert.AreEqual(0x7F800001, BitConverter.SingleToInt32Bits(LiteralParser.ParseFloat32("float32(0x7f800001)", "ldc.r4")));
        Assert.AreEqual(unchecked((int)0xFF800001), BitConverter.SingleToInt32Bits(LiteralParser.ParseFloat32("float32(0xff800001)", "ldc.r4")));
        Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(LiteralParser.ParseFloat32("-0", "ldc.r4")));
        Assert.AreEqual(1.5f, LiteralParser.ParseFloat32("1.5", "ldc.r4"));
        Assert.AreEqual(1.5f, LiteralParser.ParseFloat32("float32(1.5)", "ldc.r4"));
        Assert.AreEqual(float.PositiveInfinity, LiteralParser.ParseFloat32("inf", "ldc.r4"));
        Assert.AreEqual(1e-45f, LiteralParser.ParseFloat32("1E-45", "ldc.r4"));
        Assert.Throws<ReplException>(() => LiteralParser.ParseFloat32("float32(0x100000000)", "ldc.r4"));
    }

    /// <summary>
    /// A float32 bit pattern reaches the emitted operand bytes unchanged through both emitters.
    /// </summary>
    [TestMethod]
    public void ParseFloat32_BitsSurviveEmission()
    {
        var value = LiteralParser.ParseFloat32("float32(0x7f800001)", "ldc.r4");

        // Reflection.Emit: the cell's path. The body is persisted and read back, because a JIT
        // folds float constants through double and would quiet the NaN before any bits were read.
        var persisted = new System.Reflection.Emit.PersistedAssemblyBuilder(new System.Reflection.AssemblyName("IlReplFloatBits"), typeof(object).Assembly);
        var builder = persisted.DefineDynamicModule("IlReplFloatBits").DefineType("T", System.Reflection.TypeAttributes.Public);
        var il = builder.DefineMethod("F", System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, typeof(float), Type.EmptyTypes).GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldc_R4, value);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        builder.CreateType();
        using var stream = new MemoryStream();
        persisted.Save(stream);
        stream.Position = 0;
        var emitted = new System.Runtime.Loader.AssemblyLoadContext("IlReplFloatBits", isCollectible: true).LoadFromStream(stream);
        var emittedBytes = emitted.GetType("T")!.GetMethod("F")!.GetMethodBody()!.GetILAsByteArray()!;
        Assert.AreEqual(0x22, emittedBytes[0]);
        Assert.AreEqual(0x7F800001u, BitConverter.ToUInt32(emittedBytes, 1));

        // Cecil: a session method's path. The operand bytes are read from the written body.
        var session = new Session();
        foreach (var line in new[] { ".method float32 F() {", "ldc.r4 float32(0x7f800001)", "ret", "}" })
        {
            session.AddLine(line);
        }

        var bytes = session.Methods[0].Version.Body.GetMethodBody()!.GetILAsByteArray()!;
        Assert.AreEqual(0x22, bytes[0]);
        Assert.AreEqual(0x7F800001u, BitConverter.ToUInt32(bytes, 1));
    }
}
