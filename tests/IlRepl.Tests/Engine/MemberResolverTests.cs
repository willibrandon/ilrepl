using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="MemberResolver"/>.
/// </summary>
[TestClass]
public sealed class MemberResolverTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver());

    /// <summary>
    /// Full ILAsm references, short references, and constructors resolve.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_Forms_Resolve()
    {
        var full = MemberResolver.ResolveMethod("void [System.Console]System.Console::WriteLine(string)", Context, false).Method;
        Assert.AreEqual(typeof(Console).GetMethod("WriteLine", [typeof(string)]), full);

        var brief = MemberResolver.ResolveMethod("Math::Max(int32, int32)", Context, false).Method;
        Assert.AreEqual(typeof(Math).GetMethod("Max", [typeof(int), typeof(int)]), brief);

        var ctor = MemberResolver.ResolveMethod("instance void StringBuilder::.ctor(int32)", Context, true).Method;
        Assert.IsInstanceOfType<ConstructorInfo>(ctor);
    }

    /// <summary>
    /// Ambiguity lists candidates, and a missing overload says so.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_AmbiguousOrMissing_Explains()
    {
        var ambiguous = Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("Console::WriteLine", Context, false));
        Assert.Contains("ambiguous", ambiguous.Message);
        Assert.Contains("candidates", ambiguous.Message);
        Assert.Contains("WriteLine(int32)", ambiguous.Message);

        var missing = Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("String::Split(char)", Context, false));
        Assert.Contains("no overload", missing.Message);
    }

    /// <summary>
    /// The return type disambiguates overloads that differ only by return type in the reference.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_ReturnTypeNarrows()
    {
        var method = MemberResolver.ResolveMethod("int64 Math::Abs(int64)", Context, false).Method as MethodInfo;
        Assert.IsNotNull(method);
        Assert.AreEqual(typeof(long), method.ReturnType);
    }

    /// <summary>
    /// !0 inside a member reference means the declaring type's argument.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_DeclaringTypeParameter_Resolves()
    {
        var method = MemberResolver.ResolveMethod("instance void class [System.Collections]System.Collections.Generic.List`1<int32>::Add(!0)", Context, false).Method;
        Assert.AreEqual(typeof(List<int>).GetMethod("Add"), method);
    }

    /// <summary>
    /// Generic method instantiation with !!0 in the parameters.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_GenericMethod_Instantiates()
    {
        var method = MemberResolver.ResolveMethod("!!0 [System.Linq]System.Linq.Enumerable::First<int32>(class IEnumerable`1<!!0>)", Context, false).Method as MethodInfo;
        Assert.IsNotNull(method);
        Assert.IsTrue(method.IsGenericMethod);
        Assert.AreEqual(typeof(int), method.ReturnType);
    }

    /// <summary>
    /// Vararg references keep the extra parameter types for the call site.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_VarargSentinel_KeepsOptionalTypes()
    {
        var resolved = MemberResolver.ResolveMethod("vararg int32 Greeter.Hello::CountArgs(..., int32, string)", ContextWithGreeter(), false);
        Assert.IsNotNull(resolved.OptionalParameterTypes);
        Assert.HasCount(2, resolved.OptionalParameterTypes);
        Assert.AreEqual(3, resolved.ArgumentPopCount(false) + 1);
    }

    /// <summary>
    /// Fields resolve and a missing field lists what exists.
    /// </summary>
    [TestMethod]
    public void ResolveField_Works()
    {
        Assert.AreEqual(typeof(string).GetField("Empty"), MemberResolver.ResolveField("string String::Empty", Context));
        var ex = Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveField("int32 String::Nope", Context));
        Assert.Contains("Empty", ex.Message);
    }

    /// <summary>
    /// Describe renders a method the way the resolver accepts it.
    /// </summary>
    [TestMethod]
    public void Describe_RendersSignature()
    {
        var text = MemberResolver.Describe(typeof(Math).GetMethod("Max", [typeof(int), typeof(int)])!);
        Assert.AreEqual("int32 Math::Max(int32, int32)", text);
    }

    private static ParseContext ContextWithGreeter()
    {
        var resolver = new TypeResolver();
        resolver.Load(SampleHost.Samples.GreeterDll);
        return new ParseContext([], [], GenericContext.Empty, resolver);
    }
}
