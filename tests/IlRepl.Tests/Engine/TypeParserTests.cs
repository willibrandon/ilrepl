using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="TypeParser"/>.
/// </summary>
[TestClass]
public sealed class TypeParserTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver(), []);

    /// <summary>
    /// Primitive keywords resolve to the runtime types.
    /// </summary>
    /// <param name="text">The IL keyword.</param>
    /// <param name="expected">The expected type.</param>
    [TestMethod]
    [DataRow("int32", typeof(int))]
    [DataRow("unsigned int8", typeof(byte))]
    [DataRow("native int", typeof(nint))]
    [DataRow("float64", typeof(double))]
    [DataRow("string", typeof(string))]
    [DataRow("object", typeof(object))]
    [DataRow("typedref", typeof(TypedReference))]
    [DataRow("bool", typeof(bool))]
    public void Parse_Primitive_ReturnsRuntimeType(string text, Type expected)
    {
        Assert.AreEqual(expected, TypeParser.Parse(text, Context));
    }

    /// <summary>
    /// Assembly-qualified, short, and nested names all resolve.
    /// </summary>
    /// <param name="text">The IL type.</param>
    /// <param name="expected">The expected type.</param>
    [TestMethod]
    [DataRow("[System.Runtime]System.Text.StringBuilder", typeof(System.Text.StringBuilder))]
    [DataRow("class [System.Runtime]System.Text.StringBuilder", typeof(System.Text.StringBuilder))]
    [DataRow("StringBuilder", typeof(System.Text.StringBuilder))]
    [DataRow("valuetype [System.Runtime]System.Collections.Generic.KeyValuePair`2<int32, string>", typeof(KeyValuePair<int, string>))]
    [DataRow("List<int32>", typeof(List<int>))]
    [DataRow("Dictionary<string, List<int32>>", typeof(Dictionary<string, List<int>>))]
    public void Parse_NamedTypes_Resolve(string text, Type expected)
    {
        Assert.AreEqual(expected, TypeParser.Parse(text, Context));
    }

    /// <summary>
    /// Array, byref, and pointer suffixes compose.
    /// </summary>
    [TestMethod]
    public void Parse_Suffixes_Compose()
    {
        Assert.AreEqual(typeof(int[]), TypeParser.Parse("int32[]", Context));
        Assert.AreEqual(typeof(int[,]), TypeParser.Parse("int32[,]", Context));
        Assert.AreEqual(typeof(int[][]), TypeParser.Parse("int32[][]", Context));
        Assert.AreEqual(typeof(int).MakeByRefType(), TypeParser.Parse("int32&", Context));
        Assert.AreEqual(typeof(int).MakePointerType(), TypeParser.Parse("int32*", Context));
        Assert.AreEqual(typeof(string[]).MakeByRefType(), TypeParser.Parse("string[]&", Context));
    }

    /// <summary>
    /// The pinned modifier is reported separately from the type.
    /// </summary>
    [TestMethod]
    public void ParseAt_Pinned_ReportsPinned()
    {
        var pos = 0;
        var type = TypeParser.ParseAt("int32& pinned", ref pos, Context, out var pinned);
        Assert.AreEqual(typeof(int).MakeByRefType(), type);
        Assert.IsTrue(pinned);
    }

    /// <summary>
    /// Custom modifiers are parsed and ignored.
    /// </summary>
    [TestMethod]
    public void Parse_Modreq_IsIgnored()
    {
        var type = TypeParser.Parse("int32 modreq([System.Runtime]System.Runtime.CompilerServices.IsVolatile)", Context);
        Assert.AreEqual(typeof(int), type);
    }

    /// <summary>
    /// Function pointer types are validated and represented as native int.
    /// </summary>
    [TestMethod]
    public void Parse_FunctionPointer_IsNativeInt()
    {
        Assert.AreEqual(typeof(nint), TypeParser.Parse("method int32 *(int32, string)", Context));
    }

    /// <summary>
    /// Method generic parameters resolve through the context by index and by name.
    /// </summary>
    [TestMethod]
    public void Parse_MethodGenericParameter_ResolvesFromContext()
    {
        var parameters = PrototypeGenerics.Create(["T", "U"]);
        var context = Context.WithGenerics(new GenericContext([], parameters));
        Assert.AreEqual(parameters[1], TypeParser.Parse("!!1", context));
        Assert.AreEqual(parameters[0], TypeParser.Parse("!!T", context));
        var array = TypeParser.Parse("!!T[]", context);
        Assert.IsTrue(array.IsArray);
        Assert.AreEqual(parameters[0], array.GetElementType());
    }

    /// <summary>
    /// Unknown types and bad syntax are reported with a message meant for the prompt.
    /// </summary>
    /// <param name="text">The bad type text.</param>
    [TestMethod]
    [DataRow("NoSuchTypeAnywhere")]
    [DataRow("List<")]
    [DataRow("!!0")]
    [DataRow("int32 extra")]
    public void Parse_Invalid_ThrowsReplException(string text)
    {
        Assert.ThrowsExactly<ReplException>(() => TypeParser.Parse(text, Context));
    }

    /// <summary>
    /// A bare short name that exists in more than one namespace is reported as ambiguous.
    /// </summary>
    [TestMethod]
    public void Parse_AmbiguousShortName_SaysAmbiguous()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => TypeParser.Parse("Enumerator", Context));
        Assert.Contains("ambiguous", ex.Message);
    }

    /// <summary>
    /// Top-level splitting respects nested brackets.
    /// </summary>
    [TestMethod]
    public void SplitTopLevel_NestedGenerics_KeepsArgumentsTogether()
    {
        var parts = TypeParser.SplitTopLevel("Dictionary<string, int32>, int32[,], Func<int32, string>");
        Assert.HasCount(3, parts);
        Assert.AreEqual("Dictionary<string, int32>", parts[0]);
        Assert.AreEqual("Func<int32, string>", parts[2]);
    }
}
