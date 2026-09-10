using IlRepl.Engine;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Verifies supported IL type and member grammar while retaining source positions.
/// </summary>
/// <remarks>
/// Tests for <see cref="CilSyntaxParser"/>: the grammar reads every shape the type and member
/// parsers accept and keeps the position of every part.
/// </remarks>
[TestClass]
public sealed class CilSyntaxParserTests
{
    /// <summary>
    /// A qualified name keeps its hint, its path, and the span of each.
    /// </summary>
    [TestMethod]
    public void ParseType_QualifiedName_KeepsHintAndPath()
    {
        var text = "class [System.Runtime]System.Text.StringBuilder";
        var syntax = CilSyntaxParser.ParseType(text);
        Assert.AreEqual(TypeSyntaxKind.Named, syntax.Kind);
        Assert.IsTrue(syntax.ClassKeyword);
        Assert.AreEqual("System.Runtime", syntax.AssemblyHint);
        Assert.AreEqual("System.Text.StringBuilder", syntax.Name);
        Assert.AreEqual(0, syntax.Start);
        Assert.AreEqual(text.Length, syntax.End);
        Assert.AreEqual("[System.Runtime]", text[syntax.HintStart..syntax.HintEnd]);
        Assert.AreEqual("System.Text.StringBuilder", text[syntax.NameStart..syntax.NameEnd]);
    }

    /// <summary>
    /// Every spelling of a primitive, the multi-word ones included, reads as one keyword.
    /// </summary>
    /// <param name="text">The spelling.</param>
    /// <param name="keyword">The canonical keyword.</param>
    [TestMethod]
    [DataRow("int32", "int32")]
    [DataRow("int", "int32")]
    [DataRow("native int", "native int")]
    [DataRow("nativeint", "native int")]
    [DataRow("native unsigned int", "native uint")]
    [DataRow("unsigned int8", "uint8")]
    [DataRow("byte", "uint8")]
    [DataRow("typedref", "typedref")]
    [DataRow("decimal", "decimal")]
    public void ParseType_Primitive_IsOneKeyword(string text, string keyword)
    {
        var syntax = CilSyntaxParser.ParseType(text);
        Assert.AreEqual(TypeSyntaxKind.Primitive, syntax.Kind);
        Assert.AreEqual(keyword, syntax.Keyword);
        Assert.AreEqual(text.Length, syntax.End);
    }

    /// <summary>
    /// A word that starts a multi-word keyword but is followed by something else reads as a name.
    /// </summary>
    [TestMethod]
    public void ParseType_NativeFollowedByAName_IsNotAKeyword()
    {
        var pos = 0;
        var syntax = CilSyntaxParser.ParseTypeAt("native Foo", ref pos);
        Assert.AreEqual(TypeSyntaxKind.Named, syntax.Kind);
        Assert.AreEqual("native", syntax.Name);
        Assert.AreEqual(6, pos);
    }

    /// <summary>
    /// Suffixes wrap from the inside out in the order written.
    /// </summary>
    [TestMethod]
    public void ParseType_Suffixes_WrapInOrder()
    {
        var syntax = CilSyntaxParser.ParseType("int32[]&");
        Assert.AreEqual(TypeSyntaxKind.ByRef, syntax.Kind);
        Assert.AreEqual(TypeSyntaxKind.Array, syntax.Element!.Kind);
        Assert.IsTrue(syntax.Element.IsVector);
        Assert.AreEqual(TypeSyntaxKind.Primitive, syntax.Element.Element!.Kind);

        var rankOne = CilSyntaxParser.ParseType("int32[0...]");
        Assert.IsFalse(rankOne.IsVector);
        Assert.AreEqual(1, rankOne.Rank);
        Assert.AreEqual("0...", rankOne.Shape);
        Assert.AreEqual(2, CilSyntaxParser.ParseType("int32[,]").Rank);
        Assert.AreEqual(TypeSyntaxKind.Pointer, CilSyntaxParser.ParseType("void*").Kind);
    }

    /// <summary>
    /// Pinning and custom modifiers are kept apart from the core type, in the order written.
    /// </summary>
    [TestMethod]
    public void ParseType_PinnedAndModifiers_AreKeptApart()
    {
        var syntax = CilSyntaxParser.ParseType("int32& pinned modreq(A) modopt(B) modreq(C)");
        Assert.IsTrue(syntax.IsPinned);
        Assert.AreEqual(TypeSyntaxKind.ByRef, syntax.Unwrapped.Kind);
        Assert.AreSequenceEqual(["A", "C"], syntax.Modifiers(true).Select(m => m.Name).ToArray());
        Assert.AreSequenceEqual(["B"], syntax.Modifiers(false).Select(m => m.Name).ToArray());
    }

    /// <summary>
    /// Generic arguments nest, and each keeps its own span.
    /// </summary>
    [TestMethod]
    public void ParseType_GenericArguments_Nest()
    {
        var text = "Dictionary<string, List<int32>>";
        var syntax = CilSyntaxParser.ParseType(text);
        Assert.AreEqual("Dictionary", syntax.Name);
        Assert.HasCount(2, syntax.Arguments);
        Assert.AreEqual("string", text[syntax.Arguments[0].Start..syntax.Arguments[0].End]);
        Assert.AreEqual("List<int32>", text[syntax.Arguments[1].Start..syntax.Arguments[1].End]);
        Assert.AreEqual("int32", syntax.Arguments[1].Arguments[0].Keyword);
    }

    /// <summary>
    /// A quoted segment is decoded into the path, and nesting is kept.
    /// </summary>
    [TestMethod]
    public void ParseType_QuotedSegment_IsDecoded()
    {
        var syntax = CilSyntaxParser.ParseType("Program/'<>c'");
        Assert.AreEqual("Program/<>c", syntax.Name);
        Assert.AreEqual("Program/'<>c'".Length, syntax.NameEnd);
    }

    /// <summary>
    /// Generic parameters read by index or name, of a type or a method.
    /// </summary>
    [TestMethod]
    public void ParseType_GenericParameters_ReadByKind()
    {
        Assert.AreEqual(TypeSyntaxKind.TypeParameter, CilSyntaxParser.ParseType("!0").Kind);
        Assert.AreEqual("0", CilSyntaxParser.ParseType("!0").Reference);
        var method = CilSyntaxParser.ParseType("!!T[]");
        Assert.AreEqual(TypeSyntaxKind.Array, method.Kind);
        Assert.AreEqual(TypeSyntaxKind.MethodParameter, method.Element!.Kind);
        Assert.AreEqual("T", method.Element.Reference);
    }

    /// <summary>
    /// A function pointer keeps its convention words, return type, parameters, and sentinel.
    /// </summary>
    [TestMethod]
    public void ParseType_FunctionPointer_KeepsItsSignature()
    {
        var syntax = CilSyntaxParser.ParseType("method unmanaged cdecl int32 *(int32, ..., string)");
        Assert.AreEqual(TypeSyntaxKind.FunctionPointer, syntax.Kind);
        var signature = syntax.FunctionPointer!;
        Assert.AreSequenceEqual(["unmanaged", "cdecl"], signature.ConventionWords.ToArray());
        Assert.AreEqual("int32", signature.ReturnType.Keyword);
        Assert.HasCount(2, signature.Parameters);
        Assert.AreEqual(1, signature.SentinelIndex);
    }

    /// <summary>
    /// Bad type text is refused with the messages the prompt has always shown.
    /// </summary>
    /// <param name="text">The bad text.</param>
    /// <param name="message">The expected message.</param>
    [TestMethod]
    [DataRow("List<", "expected a type")]
    [DataRow("List<int32", "expected ',' or '>' in generic type arguments")]
    [DataRow("int32 extra", "unexpected 'extra' after type")]
    [DataRow("[System.Runtime", "unterminated '[' in type")]
    [DataRow("!", "expected an index or name after '!'")]
    [DataRow("int32 modreq int32", "expected '(' after modreq/modopt")]
    [DataRow("method int32 (int32)", "expected '*(' in function pointer type (method RetType *(Params))")]
    [DataRow("'unterminated", "unterminated quote in name")]
    [DataRow("", "expected a type")]
    public void ParseType_Invalid_ReportsTheMessage(string text, string message)
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => CilSyntaxParser.ParseType(text));
        Assert.AreEqual(message, ex.Message);
    }

    /// <summary>
    /// A full method reference keeps every part and its span.
    /// </summary>
    [TestMethod]
    public void ParseMethodReference_FullForm_KeepsEveryPart()
    {
        var text = "instance string [System.Runtime]System.String::Trim(char[])";
        var syntax = CilSyntaxParser.ParseMethodReference(text);
        Assert.IsTrue(syntax.ExplicitInstance);
        Assert.IsFalse(syntax.IsVarArg);
        Assert.IsFalse(syntax.IsSessionForm);
        Assert.AreEqual("string", syntax.ReturnType!.Keyword);
        Assert.AreEqual("System.String", syntax.DeclaringType!.Name);
        Assert.AreEqual("Trim", syntax.Name);
        Assert.AreEqual("Trim", text[syntax.NameStart..syntax.NameEnd]);
        Assert.AreEqual("::", text[syntax.SeparatorIndex..(syntax.SeparatorIndex + 2)]);
        Assert.HasCount(1, syntax.Parameters!);
        Assert.AreEqual("(char[])", text[syntax.ParametersStart..syntax.ParametersEnd]);
        Assert.AreEqual(text.Length, syntax.End);
    }

    /// <summary>
    /// The return type is optional, and so is the parameter list.
    /// </summary>
    [TestMethod]
    public void ParseMethodReference_ShortForms_AreAccepted()
    {
        var brief = CilSyntaxParser.ParseMethodReference("Math::Max(int32, int32)");
        Assert.IsNull(brief.ReturnType);
        Assert.AreEqual("Math", brief.DeclaringType!.Name);
        Assert.HasCount(2, brief.Parameters!);

        var noParameters = CilSyntaxParser.ParseMethodReference("Console::WriteLine");
        Assert.IsNull(noParameters.Parameters);
        Assert.AreEqual("WriteLine", noParameters.Name);
    }

    /// <summary>
    /// Method generic arguments and the arity form are read apart.
    /// </summary>
    [TestMethod]
    public void ParseMethodReference_GenericArgumentsAndArity_AreReadApart()
    {
        var text = "!!0 [System.Linq]System.Linq.Enumerable::First<int32>(class IEnumerable`1<!!0>)";
        var instantiated = CilSyntaxParser.ParseMethodReference(text);
        Assert.HasCount(1, instantiated.GenericArguments!);
        Assert.IsNull(instantiated.GenericArity);
        Assert.AreEqual("<int32>", text[instantiated.GenericStart..instantiated.GenericEnd]);
        Assert.AreEqual(TypeSyntaxKind.MethodParameter, instantiated.ReturnType!.Kind);

        var definition = CilSyntaxParser.ParseMethodReference("Enumerable::Select<[2]>(class IEnumerable`1<!!0>, class Func`2<!!0, !!1>)");
        Assert.AreEqual(2, definition.GenericArity);
        Assert.IsNull(definition.GenericArguments);
    }

    /// <summary>
    /// A vararg reference keeps the sentinel position; the keyword alone also marks it.
    /// </summary>
    [TestMethod]
    public void ParseMethodReference_VarArg_KeepsTheSentinel()
    {
        var syntax = CilSyntaxParser.ParseMethodReference("vararg int32 Hello::CountArgs(int32, ..., int32, string)");
        Assert.IsTrue(syntax.IsVarArg);
        Assert.AreEqual(1, syntax.SentinelIndex);
        Assert.HasCount(1, syntax.FixedParameters);
        Assert.HasCount(2, syntax.OptionalParameters!);
        Assert.IsTrue(CilSyntaxParser.ParseMethodReference("vararg int32 Hello::CountArgs(int32)").IsVarArg);
    }

    /// <summary>
    /// A quoted member name is read as one name however it is spelled inside the quotes.
    /// </summary>
    [TestMethod]
    public void ParseMethodReference_QuotedName_IsDecoded()
    {
        var syntax = CilSyntaxParser.ParseMethodReference("void Program::'<Main>b__0_0'(int32)");
        Assert.AreEqual("<Main>b__0_0", syntax.Name);
        Assert.IsTrue(syntax.NameQuoted);
        Assert.HasCount(1, syntax.Parameters!);
    }

    /// <summary>
    /// A reference with no <c>::</c> is the session form, with or without a return type.
    /// </summary>
    [TestMethod]
    public void ParseMethodReference_SessionForm_HasNoDeclaringType()
    {
        var bare = CilSyntaxParser.ParseMethodReference("Fib(int32)");
        Assert.IsTrue(bare.IsSessionForm);
        Assert.IsNull(bare.ReturnType);
        Assert.AreEqual("Fib", bare.Name);
        Assert.HasCount(1, bare.Parameters!);

        var typed = CilSyntaxParser.ParseMethodReference("int32 Fib(int32)");
        Assert.AreEqual("int32", typed.ReturnType!.Keyword);
        Assert.AreEqual("Fib", typed.Name);

        var quoted = CilSyntaxParser.ParseMethodReference("'add'()");
        Assert.AreEqual("add", quoted.Name);
        Assert.IsEmpty(quoted.Parameters!);
    }

    /// <summary>
    /// Bad member text is refused with the resolver's messages.
    /// </summary>
    /// <param name="text">The bad text.</param>
    /// <param name="message">The expected message.</param>
    [TestMethod]
    [DataRow("Console::", "missing method name")]
    [DataRow("Console::WriteLine x (string)", "unexpected 'x' before parameter list")]
    [DataRow("Console::WriteLine(string) x", "unexpected 'x' after parameter list")]
    [DataRow("Console::WriteLine x", "unexpected 'x' in method reference")]
    [DataRow("Console::WriteLine<[0]>()", "expected a generic arity such as <[1]>, got '<[0]>'")]
    [DataRow("Console::WriteLine<int32()", "unbalanced '<' in method name")]
    [DataRow("Hello::CountArgs(..., int32, ...)", "only one '...' is allowed in a parameter list")]
    [DataRow("string int32 Console::WriteLine()", "unexpected 'Console' in member reference")]
    [DataRow("Fib<int32>()", "session methods are not generic")]
    [DataRow("Fib x y", "unexpected 'y' in method reference")]
    [DataRow("1Fib()", "expected 'Type::Method(...)' in method reference (or a session method name defined with .method)")]
    public void ParseMethodReference_Invalid_ReportsTheMessage(string text, string message)
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => CilSyntaxParser.ParseMethodReference(text));
        Assert.AreEqual(message, ex.Message);
    }

    /// <summary>
    /// A field reference reads its optional type, its declaring type, and its name.
    /// </summary>
    [TestMethod]
    public void ParseFieldReference_Forms_AreRead()
    {
        var typed = CilSyntaxParser.ParseFieldReference("string [System.Runtime]System.String::Empty");
        Assert.AreEqual("string", typed.ReturnType!.Keyword);
        Assert.AreEqual("System.String", typed.DeclaringType!.Name);
        Assert.AreEqual("Empty", typed.Name);
        Assert.IsNull(typed.Parameters);

        var bare = CilSyntaxParser.ParseFieldReference("Counter::Count");
        Assert.IsNull(bare.ReturnType);
        Assert.AreEqual("Count", bare.Name);

        var quoted = CilSyntaxParser.ParseFieldReference("Counter::'value'");
        Assert.AreEqual("value", quoted.Name);
        Assert.IsTrue(quoted.NameQuoted);

        Assert.AreEqual("expected 'Type::field' in field reference", Assert.ThrowsExactly<ReplException>(()
            => CilSyntaxParser.ParseFieldReference("Count")).Message);
        Assert.AreEqual("missing field name", Assert.ThrowsExactly<ReplException>(() => CilSyntaxParser.ParseFieldReference(
            "Counter::")).Message);
    }

    /// <summary>
    /// Splitting into ranges respects nested brackets and quotes, and agrees with the string form.
    /// </summary>
    [TestMethod]
    public void SplitTopLevelRanges_AgreesWithSplitTopLevel()
    {
        var text = "Dictionary<string, int32>, 'a,b'[], Func<int32, string>";
        var ranges = CilSyntaxParser.SplitTopLevelRanges(text, 0, text.Length);
        var parts = CilSyntaxParser.SplitTopLevel(text);
        Assert.HasCount(3, ranges);
        Assert.AreSequenceEqual(parts, ranges.Select(r => text[r.Start..r.End].Trim()).ToArray());
    }
}
