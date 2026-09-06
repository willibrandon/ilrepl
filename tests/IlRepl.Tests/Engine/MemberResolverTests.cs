using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="MemberResolver"/>.
/// </summary>
[TestClass]
public sealed class MemberResolverTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver(), []);

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
        return new ParseContext([], [], GenericContext.Empty, resolver, []);
    }

    private static ParseContext ContextWith(params MethodSignature[] methods) =>
        new([], [], GenericContext.Empty, new TypeResolver(), methods);

    private static MethodSignature Fib() => new("Fib", typeof(int), [new ArgumentDeclaration(typeof(int), "n", null, "")]);

    /// <summary>
    /// A bare name resolves a session method, with the return type and the parameter list both optional.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_SessionMethod_ResolvesDefinition()
    {
        var context = ContextWith(Fib());
        foreach (var spec in new[] { "int32 Fib(int32)", "Fib(int32)", "Fib", "int32 Fib" })
        {
            var resolved = MemberResolver.ResolveMethod(spec, context, false);
            Assert.IsTrue(resolved.IsSessionMethod, spec);
            Assert.IsNull(resolved.Method, spec);
            Assert.AreEqual("Fib", resolved.Name, spec);
            Assert.AreEqual(typeof(int), resolved.ReturnType, spec);
            Assert.AreSequenceEqual([typeof(int)], resolved.ParameterTypes);
            Assert.IsTrue(resolved.IsStatic, spec);
            Assert.AreEqual("IlRepl.Cell", resolved.DeclaringTypeName, spec);
            Assert.AreEqual(1, resolved.ArgumentPopCount(false), spec);
        }
    }

    /// <summary>
    /// A stated return type must match the definition.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_SessionMethodReturnTypeMismatch_Explains()
    {
        Assert.Contains("method Fib returns int32, not int64", Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("int64 Fib(int32)", ContextWith(Fib()), false)).Message);
    }

    /// <summary>
    /// A stated parameter list must match the definition.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_SessionMethodParameterMismatch_ListsDefined()
    {
        Assert.Contains("no method Fib(string) in the session; defined: int32 Fib(int32)", Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("Fib(string)", ContextWith(Fib()), false)).Message);
        Assert.Contains("no method Fib() in the session; defined: int32 Fib(int32)", Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("Fib()", ContextWith(Fib()), false)).Message);
    }

    /// <summary>
    /// An unknown name points at .method, and lists the methods that do exist.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_UnknownSessionMethod_SuggestsDotMethod()
    {
        Assert.Contains("no method 'Fib' in the session (define one with .method, or write Type::Fib(...) for a framework method)", Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("Fib(int32)", Context, false)).Message);
        Assert.Contains("no method 'Fibb' in the session; defined: int32 Fib(int32)  (define one with .method)", Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("Fibb(int32)", ContextWith(Fib()), false)).Message);
    }

    /// <summary>
    /// newobj and instance do not apply to session methods.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_SessionMethodNewobj_Throws()
    {
        Assert.Contains("newobj needs a constructor", Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("void Fib(int32)", ContextWith(Fib()), true)).Message);
        Assert.Contains("drop 'instance'", Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("instance int32 Fib(int32)", ContextWith(Fib()), false)).Message);
        Assert.Contains("not vararg", Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("vararg int32 Fib(int32)", ContextWith(Fib()), false)).Message);
    }

    /// <summary>
    /// A dotted name without :: is a mistyped framework reference, not a session method.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_DottedNameWithoutSeparator_KeepsOriginalError()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("void Console.WriteLine(string)", Context, false));
        Assert.Contains("expected 'Type::Method(...)' in method reference", ex.Message);
        Assert.Contains("(or a session method name defined with .method)", ex.Message);
    }

    /// <summary>
    /// A quoted name and a return type with parentheses resolve a session method.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_SessionMethodQuotedOrModifiedReturn_Resolves()
    {
        var context = ContextWith(Fib());
        Assert.IsTrue(MemberResolver.ResolveMethod("int32 'Fib'(int32)", context, false).IsSessionMethod);
        Assert.IsTrue(MemberResolver.ResolveMethod("'Fib'", context, false).IsSessionMethod);
        Assert.IsTrue(MemberResolver.ResolveMethod("int32 modopt([System.Runtime]System.Runtime.CompilerServices.IsLong) Fib(int32)", context, false).IsSessionMethod);
        Assert.Contains("unexpected 'extra'", Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("int32 Fib extra", context, false)).Message);
    }
}
