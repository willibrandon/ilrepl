using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for parsing <c>.field</c> lines: access words, the contract words, offsets, constants,
/// and the ILAsm rule that no access word means privatescope.
/// </summary>
[TestClass]
public sealed class FieldDeclarationParserTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver(), []);

    private static FieldDeclaration Parse(string spec) => FieldDeclarationParser.Parse(spec, Context, ".field " + spec);

    /// <summary>
    /// A public instance field yields its name, type, and access.
    /// </summary>
    [TestMethod]
    public void Parse_Field_ReturnsTypeNameAndAttributes()
    {
        var field = Parse("public int32 X");
        Assert.AreEqual("X", field.Name);
        Assert.AreEqual(typeof(int), field.Type);
        Assert.AreEqual(FieldAttributes.Public, field.Attributes & FieldAttributes.FieldAccessMask);
        Assert.IsFalse(field.IsStatic);
        Assert.IsFalse(field.HasDefault);
        Assert.AreEqual("public int32 X", field.Describe());
    }

    /// <summary>
    /// No access word is privatescope, as ILAsm assembles it.
    /// </summary>
    [TestMethod]
    public void Parse_NoAccessWord_IsPrivateScope()
    {
        var field = Parse("int32 X");
        Assert.AreEqual(FieldAttributes.PrivateScope, field.Attributes & FieldAttributes.FieldAccessMask);
        Assert.AreEqual("privatescope int32 X", field.Describe());
    }

    /// <summary>
    /// <c>static</c>, <c>initonly</c>, and <c>literal</c> set their flags; a literal must be static and needs a value.
    /// </summary>
    [TestMethod]
    public void Parse_ContractWords()
    {
        var field = Parse("public static initonly int32 Count");
        Assert.IsTrue(field.IsStatic);
        Assert.IsTrue(field.IsInitOnly);
        Assert.Contains("must be static", Assert.ThrowsExactly<ReplException>(() => Parse("public literal int32 Max = int32(5)")).Message);
        Assert.Contains("needs its value", Assert.ThrowsExactly<ReplException>(() => Parse("public static literal int32 Max")).Message);
        Assert.Contains("not both", Assert.ThrowsExactly<ReplException>(() => Parse("public static literal initonly int32 Max = int32(5)")).Message);
    }

    /// <summary>
    /// A constant sets the default without making the field a literal; the runtime never uses
    /// it to initialize storage.
    /// </summary>
    [TestMethod]
    public void Parse_Initializer_IsIndependentOfLiteral()
    {
        var field = Parse("public static int32 X = int32(7)");
        Assert.IsTrue(field.HasDefault);
        Assert.AreEqual(7, field.DefaultValue);
        Assert.IsFalse(field.IsLiteral);
        Assert.IsTrue(field.Attributes.HasFlag(FieldAttributes.HasDefault));
        Assert.AreEqual("public static int32 X = int32(7)", field.Describe());
    }

    /// <summary>
    /// Constants parse by their wrapper, and a mismatched wrapper names both types.
    /// </summary>
    [TestMethod]
    public void Parse_ConstantForms()
    {
        Assert.AreEqual("text", Parse("public static literal string S = \"text\"").DefaultValue);
        Assert.IsTrue((bool)Parse("public static literal bool B = bool(true)").DefaultValue!);
        Assert.AreEqual(1.5, Parse("public static literal float64 D = float64(1.5)").DefaultValue);
        Assert.AreEqual((byte)200, Parse("public static literal uint8 U = uint8(200)").DefaultValue);
        Assert.AreEqual(5L, Parse("public static literal int64 L = 5").DefaultValue);
        Assert.IsNull(Parse("public static string N = nullref").DefaultValue);
        Assert.Contains("write int32(...)", Assert.ThrowsExactly<ReplException>(() => Parse("public static literal int32 X = int64(5)")).Message);
        Assert.Contains("nullref is not a valid int32", Assert.ThrowsExactly<ReplException>(() => Parse("public static int32 X = nullref")).Message);
    }

    /// <summary>
    /// An offset before the type is recorded; a negative one and one on a static field are refused.
    /// </summary>
    [TestMethod]
    public void Parse_Offset()
    {
        Assert.AreEqual(4, Parse("[4] public int32 Y").Offset);
        Assert.AreEqual("[4] public int32 Y", Parse("[4] public int32 Y").Describe());
        Assert.Contains("cannot be negative", Assert.ThrowsExactly<ReplException>(() => Parse("[-1] public int32 Y")).Message);
        Assert.Contains("no offset", Assert.ThrowsExactly<ReplException>(() => Parse("[4] public static int32 Y")).Message);
    }

    /// <summary>
    /// Custom modifiers on the field type are kept.
    /// </summary>
    [TestMethod]
    public void Parse_Modifiers_AreKept()
    {
        var field = Parse("public int32 modreq([System.Runtime]System.Runtime.CompilerServices.IsVolatile) V");
        Assert.AreEqual(typeof(int), field.Type);
        Assert.AreSequenceEqual([typeof(System.Runtime.CompilerServices.IsVolatile)], field.RequiredModifiers);
    }

    /// <summary>
    /// Words session fields do not support, a void type, and a bad name are refused.
    /// </summary>
    [TestMethod]
    public void Parse_Refusals()
    {
        Assert.Contains("not supported", Assert.ThrowsExactly<ReplException>(() => Parse("public marshal(int32) int32 X")).Message);
        Assert.Contains("cannot be void", Assert.ThrowsExactly<ReplException>(() => Parse("public void X")).Message);
        Assert.Contains("bad field name", Assert.ThrowsExactly<ReplException>(() => Parse("public int32 9x")).Message);
        Assert.Contains("one access word", Assert.ThrowsExactly<ReplException>(() => Parse("public private int32 X")).Message);
    }
}
