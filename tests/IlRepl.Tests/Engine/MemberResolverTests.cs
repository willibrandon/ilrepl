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

    /// <summary>
    /// A mistyped method name gets the nearest member the context may call, confirmed to bind.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_MistypedName_SuggestsNearest()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("String::Concta(string, string)", Context, false));
        Assert.AreEqual("no method 'Concta' on string (did you mean 'Concat'?)", ex.Message);
        Assert.AreEqual("no method 'Trmi' on string (did you mean 'Trim'?)", Assert.ThrowsExactly<ReplException>(()
            => MemberResolver.ResolveMethod("instance string String::Trmi()", Context, false)).Message);
        Assert.AreEqual("no method 'tolowerinvariant' on string (did you mean 'ToLowerInvariant'?)", Assert.ThrowsExactly<ReplException>(()
            => MemberResolver.ResolveMethod("instance string String::tolowerinvariant()", Context, false)).Message);
    }

    /// <summary>
    /// When nothing is near, the message is as it was.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_NoNearName_KeepsPlainMessage()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("String::Zqxwv()", Context, false));
        Assert.AreEqual("no method 'Zqxwv' on string", ex.Message);
    }

    /// <summary>
    /// A name that exists with other parameters is an overload problem, not a spelling one.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_WrongParameters_KeepsNoOverloadMessage()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("String::Concat(int32)", Context, false));
        Assert.StartsWith("no overload string::Concat(int32); candidates:", ex.Message);
        Assert.DoesNotContain("did you mean", ex.Message);
    }

    /// <summary>
    /// A member the context cannot call is not suggested to it.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_SuggestionIsEligibleFromTheScope()
    {
        var context = ContextWithGreeter();
        var ex = Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("instance int32 Greeter.Account::Audti()", context,
            false));
        Assert.AreEqual("no method 'Audti' on Account", ex.Message, "Audit is private to Account; the cell cannot call it");
        var visible = Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("instance void Greeter.Account::Depsit(int32)",
            context, false));
        Assert.AreEqual("no method 'Depsit' on Account (did you mean 'Deposit'?)", visible.Message);
    }

    /// <summary>
    /// A mistyped field gets the nearest field and the list of fields as before.
    /// </summary>
    [TestMethod]
    public void ResolveField_MistypedName_SuggestsAndListsFields()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveField("int32 Greeter.Counter::Cout", ContextWithGreeter(
            )));
        Assert.StartsWith("no field 'Cout' on Counter (did you mean 'Count'?); fields: ", ex.Message);
        Assert.Contains("Count", ex.Message["no field 'Cout' on Counter (did you mean 'Count'?); fields: ".Length..]);
    }

    /// <summary>
    /// The ambiguity and the missing-constructor messages are untouched.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_Ambiguous_AndConstructor_MessagesUnchanged()
    {
        var ambiguous = Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("Console::WriteLine", Context, false));
        Assert.StartsWith("ambiguous: Console::WriteLine; give parameter types. candidates:", ambiguous.Message);
        Assert.DoesNotContain("did you mean", ambiguous.Message);
        var constructor = Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("instance void String::.ctor(int32)",
            Context, true));
        Assert.StartsWith("no constructor string(int32); candidates:", constructor.Message);
        Assert.DoesNotContain("did you mean", constructor.Message);
    }

    /// <summary>
    /// Suggests declared and inherited members when lookup fails inside an open class.
    /// </summary>
    /// <remarks>
    /// Inside a class being written, a mistyped member is matched against the declared members
    /// and what the base offers, and the list of methods stays.
    /// </remarks>
    [TestMethod]
    public void ResolveMethod_OpenClassTypo_SuggestsDeclaredOrInherited()
    {
        var session = IlLines.Load(
            ".class public Base {",
            ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }",
            ".method family instance int32 Inherited() { ldc.i4 1; ret }",
            "}",
            ".class public Point extends Base {",
            ".method public instance int32 Sum() { ldc.i4 3; ret }",
            ".method public instance int32 Twice() {",
            "ldarg.0");
        // A reference with a full signature declares the member ahead; one without a return type must name a member that exists.
        var own = Assert.ThrowsExactly<ReplException>(() => session.AddLine("call Point::Sumx()"));
        Assert.StartsWith("no method 'Sumx' on Point (did you mean 'Sum'?); methods: ", own.Message);
        var inherited = Assert.ThrowsExactly<ReplException>(() => session.AddLine("call Point::Inherted()"));
        Assert.StartsWith("no method 'Inherted' on Point (did you mean 'Inherited'?); methods: ", inherited.Message);
        var undeclared = Assert.ThrowsExactly<ReplException>(() => session.AddLine("call Point::Nothing()"));
        Assert.StartsWith("no method 'Nothing' on Point; methods: ", undeclared.Message);
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
        Assert.Contains("no method 'Fibb' in the session (did you mean 'Fib'?); defined: int32 Fib(int32)  (define one with .method)",
            Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("Fibb(int32)", ContextWith(Fib()), false)).Message);
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

    /// <summary>
    /// int32[] and int32[0...] are different parameter types.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_SessionMethodArrayKind_MustMatch()
    {
        var first = new MethodSignature("First", typeof(int), [new ArgumentDeclaration(typeof(int[]), "a", null, "")]);
        var context = ContextWith(first);
        Assert.IsTrue(MemberResolver.ResolveMethod("First(int32[])", context, false).IsSessionMethod);
        Assert.Contains("no method First(int32[0...]) in the session; defined: int32 First(int32[])", Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod("First(int32[0...])", context, false)).Message);
    }

    /// <summary>
    /// Quoted names, as listings print them, resolve: a method named like an opcode, a compiler-made
    /// nested type and member, and a field whose name starts with angle brackets.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_QuotedNames_Resolve()
    {
        var resolver = new TypeResolver();
        resolver.Load(SampleHost.Samples.FixturesDll);
        var context = new ParseContext([], [], GenericContext.Empty, resolver, []);

        var add = MemberResolver.ResolveMethod("int32 [Fixtures]Fixtures.Shapes::'add'(int32, int32)", context, false);
        Assert.AreEqual("add", add.Method!.Name);
        var lambda = MemberResolver.ResolveMethod("instance int32 [Fixtures]Fixtures.Shapes/'<>c'::'<Doubled>b__0_0'(int32)", context, false);
        Assert.AreEqual("<Doubled>b__0_0", lambda.Method!.Name);
        Assert.AreEqual("<>c", lambda.Method.DeclaringType!.Name);
        var field = MemberResolver.ResolveField("class [System.Runtime]System.Func`2<int32, int32> [Fixtures]Fixtures.Shapes/'<>c'::'<>9__0_0'", context);
        Assert.AreEqual("<>9__0_0", field.Name);
        Assert.AreEqual("<>c", field.DeclaringType!.Name);
        var closure = TypeParser.Parse("class [Fixtures]Fixtures.Shapes/'<>c__DisplayClass13_0`1'<int32>", context);
        Assert.IsTrue(closure.IsGenericType);
        Assert.AreEqual("<>c__DisplayClass13_0`1", closure.GetGenericTypeDefinition().Name);
    }

    /// <summary>
    /// The arity form names a generic method definition without instantiating it, and never a non-generic overload.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_ArityMarker_ResolvesTheDefinition()
    {
        var resolver = new TypeResolver();
        resolver.Load(SampleHost.Samples.FixturesDll);
        var context = new ParseContext([], [], GenericContext.Empty, resolver, []);
        var larger = MemberResolver.ResolveMethod("!!0 [Fixtures]Fixtures.Shapes::Larger<[1]>(!!0, !!0)", context, false);
        Assert.IsTrue(larger.Method!.IsGenericMethodDefinition);
        Assert.AreEqual("Larger", larger.Method.Name);
        var bare = MemberResolver.ResolveMethod("[Fixtures]Fixtures.Shapes::Larger<[1]>", context, false);
        Assert.IsTrue(bare.Method!.IsGenericMethodDefinition);
        Assert.Throws<ReplException>(() => MemberResolver.ResolveMethod("!!0 [Fixtures]Fixtures.Shapes::Larger<[2]>(!!0, !!0)", context, false));
        Assert.Throws<ReplException>(() => MemberResolver.ResolveMethod("[Fixtures]Fixtures.Shapes::Larger<[x]>", context, false));

        // An assembly-qualified array is a type argument, not an arity, and whitespace before the arguments is allowed.
        var empty = MemberResolver.ResolveMethod("!!0[] [System.Runtime]System.Array::Empty<[System.Runtime]System.String[]>()", context, false);
        Assert.AreEqual(typeof(string[]), empty.Method!.GetGenericArguments()[0]);
        var spaced = MemberResolver.ResolveMethod("[System.Runtime]System.Array::Empty <string>()", context, false);
        Assert.AreEqual(typeof(string), spaced.Method!.GetGenericArguments()[0]);
        var spacedParen = MemberResolver.ResolveMethod("int32 [Fixtures]Fixtures.Shapes::add (int32, int32)", context, false);
        Assert.AreEqual("add", spacedParen.Method!.Name);
    }
}
