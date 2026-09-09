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
    /// A mistyped type names the nearest type, and the load hint goes.
    /// </summary>
    [TestMethod]
    public void Parse_MistypedType_NamesTheNearest()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => TypeParser.Parse("StringBuilderr", Context));
        Assert.AreEqual("type 'StringBuilderr' not found (did you mean 'StringBuilder'?)", ex.Message);
        Assert.AreEqual("type 'Cosnole' not found (did you mean 'Console'?)", Assert.ThrowsExactly<ReplException>(() => TypeParser.Parse("Cosnole", Context)).Message);
        Assert.AreEqual("type 'Xonsole' not found (did you mean 'Console'?)", Assert.ThrowsExactly<ReplException>(() => TypeParser.Parse("Xonsole", Context)).Message);
    }

    /// <summary>
    /// When nothing is near, the message still points at .load.
    /// </summary>
    [TestMethod]
    public void Parse_NothingNear_KeepsTheLoadHint()
    {
        Assert.AreEqual("type 'NoSuchTypeAnywhere' not found (load its assembly with .load)", Assert.ThrowsExactly<ReplException>(() => TypeParser.Parse("NoSuchTypeAnywhere", Context)).Message);
        Assert.AreEqual("type 'Foo' not found in [Nope] (load its assembly with .load)", Assert.ThrowsExactly<ReplException>(() => TypeParser.Parse("[Nope]Foo", Context)).Message);
    }

    /// <summary>
    /// A nearest name that is ambiguous as a short name is spelled qualified.
    /// </summary>
    [TestMethod]
    public void Parse_AmbiguousNearest_IsSpelledQualified()
    {
        var resolver = new TypeResolver();
        resolver.Load(SampleHost.Samples.GreeterDll);
        var context = new ParseContext([], [], GenericContext.Empty, resolver, []);
        var ex = Assert.ThrowsExactly<ReplException>(() => TypeParser.Parse("Countr", context));
        Assert.AreEqual("type 'Countr' not found (did you mean 'Greeter.Counter'?)", ex.Message);
        Assert.Contains("ambiguous", Assert.ThrowsExactly<ReplException>(() => TypeParser.Parse("Counter", context)).Message);
    }

    /// <summary>
    /// Short names that resolve today still resolve, and the one that is ambiguous still is.
    /// </summary>
    [TestMethod]
    public void Parse_ShortNamesThatResolveToday_StillResolve()
    {
        Assert.AreEqual(typeof(System.Net.WebUtility), TypeParser.Parse("WebUtility", Context));
        Assert.AreEqual(typeof(System.Collections.Immutable.ImmutableArray<>), TypeParser.Parse("ImmutableArray`1", Context));
        var ex = Assert.ThrowsExactly<ReplException>(() => TypeParser.Parse("JsonSerializer", Context));
        Assert.Contains("ambiguous", ex.Message);
    }

    /// <summary>
    /// A mistyped session type is suggested by its own name.
    /// </summary>
    [TestMethod]
    public void Parse_SessionTypeTypo_SuggestsTheSessionType()
    {
        var session = IlLines.Load(".class public Point { }");
        var ex = Assert.ThrowsExactly<ReplException>(() => session.AddLine("newobj instance void Poit::.ctor()"));
        Assert.AreEqual("type 'Poit' not found (did you mean 'Point'?)", ex.Message);
    }

    /// <summary>
    /// A nested session type the cell cannot use is not suggested to it, and is to a body that can.
    /// </summary>
    [TestMethod]
    public void Parse_MistypedType_NeverSuggestsAnInaccessibleNestedSessionType()
    {
        var session = IlLines.Load(
            ".class public Outer {",
            ".class nested private Secret { }",
            "}");
        var ex = Assert.ThrowsExactly<ReplException>(() => session.AddLine("newobj instance void Secrt::.ctor()"));
        Assert.StartsWith("type 'Secrt' not found", ex.Message);
        Assert.DoesNotContain("Secret", ex.Message, "the cell cannot use a nested private type, so it is not offered one");
    }

    /// <summary>
    /// A header inside a class judges its suggestions from that class, not from the cell.
    /// </summary>
    [TestMethod]
    public void Parse_HeaderContext_UsesTheEnclosingScope()
    {
        var session = IlLines.Load(
            ".class public Outer {",
            ".class nested private Secret { }");
        // Outer/Secrt would name a nested type declared later; a bare Secrt names nothing, and the block may see Secret.
        var ex = Assert.ThrowsExactly<ReplException>(() => session.AddLine(".field public static class Secrt Holder"));
        Assert.AreEqual("type 'Secrt' not found (did you mean 'Secret'?)", ex.Message);
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

    /// <summary>
    /// A rank-1 array with bounds renders apart from a vector, in both display and ILAsm form.
    /// </summary>
    [TestMethod]
    public void Arrays_VectorAndRankOne_RenderDifferently()
    {
        var vector = TypeParser.Parse("int32[]", Context);
        var rankOne = TypeParser.Parse("int32[0...]", Context);
        Assert.IsTrue(vector.IsSZArray);
        Assert.IsFalse(rankOne.IsSZArray);
        Assert.AreEqual("int32[]", TypeNameFormatter.Pretty(vector));
        Assert.AreEqual("int32[0...]", TypeNameFormatter.Pretty(rankOne));
        Assert.AreEqual("int32[0...]", TypeNameFormatter.IlAsm(rankOne));
        Assert.AreEqual("int32[,]", TypeNameFormatter.Pretty(TypeParser.Parse("int32[,]", Context)));
    }
}
