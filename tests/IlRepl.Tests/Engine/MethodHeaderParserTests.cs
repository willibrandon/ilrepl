using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="MethodHeaderParser"/>.
/// </summary>
[TestClass]
public sealed class MethodHeaderParserTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver(), []);

    /// <summary>
    /// A header yields the name, return type, named parameters, and whether the brace was on the line.
    /// </summary>
    [TestMethod]
    public void Parse_Header_ReturnsSignatureAndBrace()
    {
        var signature = MethodHeaderParser.Parse(" int32 Fib(int32 n) {", Context, out var opensBlock);
        Assert.IsTrue(opensBlock);
        Assert.AreEqual("Fib", signature.Name);
        Assert.AreEqual(typeof(int), signature.ReturnType);
        Assert.HasCount(1, signature.Parameters);
        Assert.AreEqual("n", signature.Parameters[0].Name);
        Assert.AreEqual(typeof(int), signature.Parameters[0].Type);
        Assert.AreEqual("int32 Fib(int32)", signature.Describe());
        Assert.AreEqual("int32 Fib(int32 n)", signature.DescribeWithNames());
    }

    /// <summary>
    /// Without a brace the block is still opened; the brace may follow on its own line.
    /// </summary>
    [TestMethod]
    public void Parse_HeaderWithoutBrace_SetsOpensBlockFalse()
    {
        var signature = MethodHeaderParser.Parse(" string Greet(string name)", Context, out var opensBlock);
        Assert.IsFalse(opensBlock);
        Assert.AreEqual("Greet", signature.Name);
    }

    /// <summary>
    /// ILAsm accessibility and implementation words are accepted and ignored.
    /// </summary>
    [TestMethod]
    public void Parse_IlAsmModifiers_AreIgnored()
    {
        var signature = MethodHeaderParser.Parse(" public static hidebysig int32 Add(int32 a, int32 b) cil managed {", Context, out var opensBlock);
        Assert.IsTrue(opensBlock);
        Assert.AreEqual("int32 Add(int32 a, int32 b)", signature.DescribeWithNames());
    }

    /// <summary>
    /// Parameters need no names, and [in]/[out] markers are stripped.
    /// </summary>
    [TestMethod]
    public void Parse_UnnamedParameters_AreAllowed()
    {
        var signature = MethodHeaderParser.Parse(" int64 Mul(int64, [in] int64) {", Context, out _);
        Assert.HasCount(2, signature.Parameters);
        Assert.IsNull(signature.Parameters[0].Name);
        Assert.IsNull(signature.Parameters[1].Name);
        Assert.AreEqual("int64 Mul(int64, int64)", signature.DescribeWithNames());
    }

    /// <summary>
    /// void returns nothing, and multi-word primitives parse.
    /// </summary>
    [TestMethod]
    public void Parse_VoidReturn_IsAllowed()
    {
        var signature = MethodHeaderParser.Parse(" void Hi() {", Context, out _);
        Assert.AreEqual(typeof(void), signature.ReturnType);
        Assert.IsEmpty(signature.Parameters);

        var native = MethodHeaderParser.Parse(" native int Ptr(unsigned int8 b)", Context, out _);
        Assert.AreEqual(typeof(nint), native.ReturnType);
        Assert.AreEqual(typeof(byte), native.Parameters[0].Type);
    }

    /// <summary>
    /// Run and Invoke belong to the cell.
    /// </summary>
    [TestMethod]
    public void Parse_ReservedNames_Throw()
    {
        Assert.Contains("'Run' is reserved", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" object Run() {", Context, out _)).Message);
        Assert.Contains("'Invoke' is reserved", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" void Invoke() {", Context, out _)).Message);
    }

    /// <summary>
    /// A name must be an identifier.
    /// </summary>
    [TestMethod]
    public void Parse_BadName_Throws()
    {
        Assert.Contains("bad method name '2x'", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" int32 2x() {", Context, out _)).Message);
        Assert.Contains("bad method name", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" int32 Foo<T>() {", Context, out _)).Message);
    }

    /// <summary>
    /// Parameter names are unique within the header.
    /// </summary>
    [TestMethod]
    public void Parse_DuplicateParameter_Throws()
    {
        Assert.Contains("parameter 'n' is already declared", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" int32 F(int32 n, int32 n) {", Context, out _)).Message);
    }

    /// <summary>
    /// A parameter cannot be void.
    /// </summary>
    [TestMethod]
    public void Parse_VoidParameter_Throws()
    {
        Assert.Contains("cannot be void", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" int32 F(void x) {", Context, out _)).Message);
    }

    /// <summary>
    /// A header without a parameter list or without a return type shows the usage.
    /// </summary>
    [TestMethod]
    public void Parse_MissingParameterList_ShowsUsage()
    {
        Assert.Contains("usage: .method", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" int32 Fib {", Context, out _)).Message);
        Assert.Contains("usage: .method", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" Fib() {", Context, out _)).Message);
        Assert.Contains("usage: .method", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse("", Context, out _)).Message);
    }

    /// <summary>
    /// Session methods are static.
    /// </summary>
    [TestMethod]
    public void Parse_InstanceKeyword_Throws()
    {
        Assert.Contains("remove 'instance'", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" instance int32 F() {", Context, out _)).Message);
    }

    /// <summary>
    /// Session methods are not vararg, whether by keyword or by sentinel.
    /// </summary>
    [TestMethod]
    public void Parse_VarargSentinel_Throws()
    {
        Assert.Contains("cannot be vararg", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" vararg int32 F() {", Context, out _)).Message);
        Assert.Contains("cannot be vararg", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" int32 F(int32, ...) {", Context, out _)).Message);
    }

    /// <summary>
    /// Only the ILAsm implementation words may follow the parameter list.
    /// </summary>
    [TestMethod]
    public void Parse_TrailingJunk_Throws()
    {
        Assert.Contains("unexpected 'extra' after the parameter list", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" int32 F() extra {", Context, out _)).Message);
    }

    /// <summary>
    /// The cell's generic parameters are not in scope for a method header.
    /// </summary>
    [TestMethod]
    public void Parse_CellGenericParameter_Throws()
    {
        Assert.Contains("generic parameter", Assert.ThrowsExactly<ReplException>(() => MethodHeaderParser.Parse(" !!T Id(!!T x) {", Context, out _)).Message);
    }

    /// <summary>
    /// Only [in], [out], and [opt] are parameter attributes; an assembly qualifier belongs to the type.
    /// </summary>
    [TestMethod]
    public void Parse_AssemblyQualifiedParameter_KeepsQualifier()
    {
        var signature = MethodHeaderParser.Parse(" void F([System.Runtime]System.Object o, [in] int32 x, [out] [System.Runtime]System.String s) {", Context, out _);
        Assert.AreSequenceEqual([typeof(object), typeof(int), typeof(string)], signature.ParameterTypes);
        Assert.AreEqual("o", signature.Parameters[0].Name);
        Assert.AreEqual("s", signature.Parameters[2].Name);
    }

    /// <summary>
    /// The return type is parsed as a type, so its own parentheses do not end the header.
    /// </summary>
    [TestMethod]
    public void Parse_ReturnTypeWithParentheses_IsParsed()
    {
        var modopt = MethodHeaderParser.Parse(" int32 modopt([System.Runtime]System.Runtime.CompilerServices.IsLong) F() {", Context, out var opensBlock);
        Assert.AreEqual(typeof(int), modopt.ReturnType);
        Assert.AreEqual("F", modopt.Name);
        Assert.IsTrue(opensBlock);

        var pointer = MethodHeaderParser.Parse(" method int32 *(int32) G(int32 x)", Context, out _);
        Assert.AreEqual(typeof(nint), pointer.ReturnType);
        Assert.AreEqual("G", pointer.Name);
        Assert.HasCount(1, pointer.Parameters);
    }

    /// <summary>
    /// A quoted name is stored without its quotes.
    /// </summary>
    [TestMethod]
    public void Parse_QuotedName_IsUnquoted()
    {
        var signature = MethodHeaderParser.Parse(" int32 'F'(int32 'value') {", Context, out _);
        Assert.AreEqual("F", signature.Name);
        Assert.AreEqual("value", signature.Parameters[0].Name);
    }
}
