using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for parsing <c>.method</c> headers inside a <c>.class</c> block, where the ILAsm words
/// mean what ILAsm says: instance unless static, no access unless an access word is written,
/// constructors by name, and the virtual words recorded.
/// </summary>
[TestClass]
public sealed class MemberHeaderParserTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver(), []);
    private static readonly TypeHeader Class = TypeHeaderParser.Parse("public Point {", nested: false);
    private static readonly TypeHeader Abstract = TypeHeaderParser.Parse("public abstract Shape {", nested: false);
    private static readonly TypeHeader Interface = TypeHeaderParser.Parse("interface public abstract IArea {", nested: false);

    private static MethodSignature Parse(string spec, TypeHeader? owner = null, bool expectOpen = true)
    {
        var signature = MethodHeaderParser.ParseMember(spec, Context, owner ?? Class, out var opens, out _, out _);
        Assert.AreEqual(expectOpen, opens);
        return signature;
    }

    /// <summary>
    /// A method is an instance method unless static is written, and privatescope unless an access word is.
    /// </summary>
    [TestMethod]
    public void Parse_InstanceAndAccessDefaults()
    {
        var instance = Parse("int32 Sum() {");
        Assert.IsFalse(instance.IsStatic);
        Assert.IsTrue(instance.CallingConvention.HasFlag(CallingConventions.HasThis));
        Assert.AreEqual(MethodAttributes.PrivateScope, instance.Attributes & MethodAttributes.MemberAccessMask);
        Assert.AreEqual("instance int32 Sum()", instance.DescribeMember());
        var explicitInstance = Parse("public instance int32 Sum() {");
        Assert.IsFalse(explicitInstance.IsStatic);
        Assert.AreEqual(MethodAttributes.Public, explicitInstance.Attributes & MethodAttributes.MemberAccessMask);
        var statics = Parse("public static int32 Make(int32 x) {");
        Assert.IsTrue(statics.IsStatic);
        Assert.AreEqual("static int32 Make(int32)", statics.DescribeMember());
        Assert.Contains("static or instance, not both", Assert.ThrowsExactly<ReplException>(() => Parse("public static instance void M() {")).Message);
    }

    /// <summary>
    /// Constructors and type initializers get their special-name flags and are checked for shape.
    /// </summary>
    [TestMethod]
    public void Parse_Constructors()
    {
        var ctor = Parse("public instance void .ctor(int32 x) {");
        Assert.AreEqual(".ctor", ctor.Name);
        Assert.IsTrue(ctor.Attributes.HasFlag(MethodAttributes.SpecialName | MethodAttributes.RTSpecialName));
        var cctor = Parse("static void .cctor() {");
        Assert.IsTrue(cctor.IsStatic);
        Assert.Contains(".ctor must be", Assert.ThrowsExactly<ReplException>(() => Parse("public static void .ctor() {")).Message);
        Assert.Contains(".cctor must be", Assert.ThrowsExactly<ReplException>(() => Parse("static void .cctor(int32 x) {")).Message);
        Assert.Contains("no constructor", Assert.ThrowsExactly<ReplException>(() => Parse("public instance void .ctor() {", Interface)).Message);
    }

    /// <summary>
    /// The virtual words are recorded and checked against each other and the owner.
    /// </summary>
    [TestMethod]
    public void Parse_VirtualWords()
    {
        var virt = Parse("public virtual newslot final hidebysig instance string Name() {");
        Assert.IsTrue(virt.Attributes.HasFlag(MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final | MethodAttributes.HideBySig));
        var abstractMethod = Parse("public abstract virtual instance int32 Area() {", Abstract);
        Assert.IsTrue(abstractMethod.Attributes.HasFlag(MethodAttributes.Abstract));
        Assert.Contains("must be virtual", Assert.ThrowsExactly<ReplException>(() => Parse("public abstract instance int32 Area() {", Abstract)).Message);
        Assert.Contains("add 'abstract' to the .class header", Assert.ThrowsExactly<ReplException>(() => Parse("public abstract virtual instance int32 Area() {")).Message);
        Assert.Contains("needs 'virtual'", Assert.ThrowsExactly<ReplException>(() => Parse("public newslot instance int32 Area() {")).Message);
        Assert.Contains("cannot be virtual outside an interface", Assert.ThrowsExactly<ReplException>(() => Parse("public static virtual int32 Zero() {")).Message);
        var staticVirtual = Parse("public static virtual int32 One() {", Interface);
        Assert.IsTrue(staticVirtual.IsStatic && staticVirtual.Attributes.HasFlag(MethodAttributes.Virtual));
        var slot = Parse("public abstract virtual instance float64 Area() {", Interface);
        Assert.IsTrue(slot.Attributes.HasFlag(MethodAttributes.NewSlot), "an interface's virtual instance member introduces a slot");
    }

    /// <summary>
    /// Parameter attributes and custom modifiers are kept, and a vararg member is allowed.
    /// </summary>
    [TestMethod]
    public void Parse_ParameterDetails()
    {
        var signature = Parse("public static int32 modreq([System.Runtime]System.Runtime.CompilerServices.IsVolatile) M([in] int32 modopt([System.Runtime]System.Runtime.CompilerServices.IsVolatile) x, [out] int32& y) {");
        Assert.AreSequenceEqual([typeof(System.Runtime.CompilerServices.IsVolatile)], signature.ReturnRequiredModifiers);
        Assert.AreEqual(ParameterAttributes.In, signature.Parameters[0].Attributes);
        Assert.AreSequenceEqual([typeof(System.Runtime.CompilerServices.IsVolatile)], signature.Parameters[0].OptionalModifiers);
        Assert.AreEqual(ParameterAttributes.Out, signature.Parameters[1].Attributes);
        Assert.IsTrue(signature.Parameters[1].Type.IsByRef);
        var vararg = Parse("public vararg int32 Count(int32 first, ...) {");
        Assert.IsTrue(vararg.CallingConvention.HasFlag(CallingConventions.VarArgs));
        Assert.HasCount(1, vararg.Parameters);
        Assert.Contains("needs the vararg calling convention", Assert.ThrowsExactly<ReplException>(() => Parse("public int32 Count(int32 first, ...) {")).Message);
    }

    /// <summary>
    /// Implementation words map to flags; the ones session methods cannot have are refused.
    /// </summary>
    [TestMethod]
    public void Parse_ImplementationWords()
    {
        var signature = Parse("public instance void M() cil managed noinlining synchronized {");
        Assert.IsTrue(signature.ImplAttributes.HasFlag(MethodImplAttributes.NoInlining | MethodImplAttributes.Synchronized));
        Assert.Contains("not supported", Assert.ThrowsExactly<ReplException>(() => Parse("public instance void M() runtime managed {")).Message);
        Assert.Contains("not supported", Assert.ThrowsExactly<ReplException>(() => Parse("public pinvokeimpl(\"x\") static void M() {")).Message);
    }

    /// <summary>
    /// A generic method's parameters resolve inside its own signature, and the method name may be quoted.
    /// </summary>
    [TestMethod]
    public void Parse_GenericMethodAndQuotedName()
    {
        var signature = MethodHeaderParser.ParseMember("public instance !!0 Map<T>(!!0 x) {", Context, Class, out _, out _, out var typeParameters);
        Assert.HasCount(1, typeParameters);
        Assert.AreSame(typeParameters[0], signature.ReturnType);
        Assert.AreSame(typeParameters[0], signature.Parameters[0].Type);
        Assert.AreEqual("T", signature.TypeParameters[0].Name);
        Assert.AreEqual("add", Parse("public instance int32 'add'(int32 x) {").Name);
        Assert.AreEqual("Run", Parse("public static object Run() {").Name, "the cell's reserved names are free inside a class");
    }

    /// <summary>
    /// The header may end with <c>{ }</c> for an empty body, and an enum declares no methods.
    /// </summary>
    [TestMethod]
    public void Parse_EmptyBodyAndEnum()
    {
        MethodHeaderParser.ParseMember("public abstract virtual instance int32 Area() { }", Context, Abstract, out var opens, out var closes, out _);
        Assert.IsTrue(opens);
        Assert.IsTrue(closes);
        var enumHeader = TypeHeaderParser.Parse("public enum Color {", nested: false);
        Assert.Contains("enum cannot declare methods", Assert.ThrowsExactly<ReplException>(() => Parse("public static int32 M() {", enumHeader)).Message);
    }
}
