using IlRepl.Engine;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Verifies runtime and metadata binding agree on the identities selected by the same source text.
/// </summary>
/// <remarks>
/// The runtime scope and the snapshot scope bind the same text to the same symbol: whatever the
/// resolver finds through reflection, a preview finds through metadata, with the same identity.
/// </remarks>
[TestClass]
public sealed class BindingParityTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver(), []);

    private static (RuntimeBindingScope Runtime, SnapshotBindingScope Snapshot, BindingSnapshot Captured) Scopes(ParseContext context)
    {
        var snapshot = BindingSnapshot.Capture(context);
        return (new RuntimeBindingScope(context), new SnapshotBindingScope(snapshot), snapshot);
    }

    private static void AssertSameType(ParseContext context, string text)
    {
        var (runtime, snapshot, captured) = Scopes(context);
        using (captured)
        {
            var syntax = CilSyntaxParser.ParseType(TypeParser.Normalize(text));
            var fromRuntime = SymbolBinder.BindType(syntax, runtime).Type;
            var fromSnapshot = SymbolBinder.BindType(syntax, snapshot).Type;
            Assert.AreEqual(fromRuntime, fromSnapshot, text);
            Assert.AreEqual(SymbolRenderer.Pretty(fromRuntime), SymbolRenderer.Pretty(fromSnapshot), text);
            if (fromRuntime.Kind == TypeSymbolKind.Array)
            {
                // Reflection retains the rank and element, while the bound symbols also retain signature bounds.
                Assert.AreEqual(TypeParser.Parse(text, context), RuntimeBindingAdapter.Materialize(fromSnapshot), text);
            }
            else if (fromRuntime.Kind != TypeSymbolKind.FunctionPointer)
            {
                // A function pointer is native int to the runtime model; the symbol keeps its signature.
                Assert.AreEqual(RuntimeSymbolImporter.Import(TypeParser.Parse(text, context)), fromSnapshot, text);
            }
        }
    }

    private static void AssertSameMethod(ParseContext context, string text, bool wantConstructor = false)
    {
        var (runtime, snapshot, captured) = Scopes(context);
        using (captured)
        {
            var syntax = CilSyntaxParser.ParseMethodReference(TypeParser.Normalize(text).Trim());
            var fromRuntime = SymbolBinder.BindMethodReference(syntax, runtime, wantConstructor);
            var fromSnapshot = SymbolBinder.BindMethodReference(syntax, snapshot, wantConstructor);
            Assert.AreEqual(fromRuntime.Method, fromSnapshot.Method, text);
            Assert.AreEqual(fromRuntime.Method.ReturnType, fromSnapshot.Method.ReturnType, text);
            Assert.IsTrue(SymbolIdentity.SequenceEqual(fromRuntime.Method.ParameterTypes, fromSnapshot.Method.ParameterTypes), text);
            Assert.AreEqual(fromRuntime.Method.IsStatic, fromSnapshot.Method.IsStatic, text);
            Assert.AreEqual(fromRuntime.OptionalParameterTypes is null, fromSnapshot.OptionalParameterTypes is null, text);
            var resolved = MemberResolver.ResolveMethod(text, context, wantConstructor);
            if (resolved.Method is { } method && !fromRuntime.Method.Definition.IsDeclaration)
            {
                // A member of a type being written comes back as its builder, whose identity the importer already gave the symbol.
                Assert.AreEqual(RuntimeSymbolImporter.Import(method).Definition, fromSnapshot.Method.Definition, text);
            }
        }
    }

    private static void AssertSameField(ParseContext context, string text)
    {
        var (runtime, snapshot, captured) = Scopes(context);
        using (captured)
        {
            var syntax = CilSyntaxParser.ParseFieldReference(TypeParser.Normalize(text).Trim());
            var fromRuntime = SymbolBinder.BindFieldReference(syntax, runtime);
            var fromSnapshot = SymbolBinder.BindFieldReference(syntax, snapshot);
            Assert.AreEqual(fromRuntime, fromSnapshot, text);
            Assert.AreEqual(fromRuntime.FieldType, fromSnapshot.FieldType, text);
            var resolved = MemberResolver.ResolveField(text, context);
            if (!fromRuntime.Definition.IsDeclaration)
            {
                Assert.AreEqual(RuntimeSymbolImporter.Import(resolved).Definition, fromSnapshot.Definition, text);
            }
        }
    }

    /// <summary>
    /// Every spelling of a type the parser accepts binds to the same symbol in both scopes.
    /// </summary>
    /// <param name="text">The type text.</param>
    [TestMethod]
    [DataRow("int32")]
    [DataRow("unsigned int8")]
    [DataRow("native int")]
    [DataRow("string")]
    [DataRow("object")]
    [DataRow("typedref")]
    [DataRow("decimal")]
    [DataRow("[System.Runtime]System.Text.StringBuilder")]
    [DataRow("class [System.Runtime]System.Text.StringBuilder")]
    [DataRow("StringBuilder")]
    [DataRow("System.Text.StringBuilder")]
    [DataRow("valuetype [System.Runtime]System.Collections.Generic.KeyValuePair`2<int32, string>")]
    [DataRow("List<int32>")]
    [DataRow("Dictionary<string, List<int32>>")]
    [DataRow("int32[]")]
    [DataRow("int32[,]")]
    [DataRow("int32[0...]")]
    [DataRow("string[]&")]
    [DataRow("void*")]
    [DataRow("int32 modreq([System.Runtime]System.Runtime.CompilerServices.IsVolatile)")]
    [DataRow("method int32 *(int32, string)")]
    [DataRow("[System.Runtime]System.Environment/SpecialFolder")]
    [DataRow("System.Environment/SpecialFolder")]
    [DataRow("class [System.Collections]System.Collections.Generic.List`1")]
    [DataRow("Console")]
    [DataRow("[System.Console]System.Console")]
    [DataRow("WebUtility")]
    [DataRow("ImmutableArray`1")]
    [DataRow("Task")]
    [DataRow("Regex")]
    [DataRow("BigInteger")]
    [DataRow("Vector`1")]
    public void BindType_RuntimeAndSnapshot_Agree(string text) => AssertSameType(Context, text);

    /// <summary>
    /// A name that fails in the runtime scope fails in the snapshot scope with the same message.
    /// </summary>
    /// <param name="text">The type text.</param>
    [TestMethod]
    [DataRow("NoSuchTypeAnywhere")]
    [DataRow("[NoSuchAssembly]Foo.Bar")]
    [DataRow("Enumerator")]
    [DataRow("List<int32, string>")]
    [DataRow("String<int32>")]
    public void BindType_Failures_Agree(string text)
    {
        var (runtime, snapshot, captured) = Scopes(Context);
        using (captured)
        {
            var syntax = CilSyntaxParser.ParseType(text);
            var fromRuntime = Assert.ThrowsExactly<ReplException>(() => SymbolBinder.BindType(syntax, runtime));
            var fromSnapshot = Assert.ThrowsExactly<ReplException>(() => SymbolBinder.BindType(syntax, snapshot));
            Assert.AreEqual(fromRuntime.Message, fromSnapshot.Message, text);
        }
    }

    /// <summary>
    /// Every member reference the resolver accepts binds to the same member in both scopes.
    /// </summary>
    /// <param name="text">The reference.</param>
    [TestMethod]
    [DataRow("void [System.Console]System.Console::WriteLine(string)")]
    [DataRow("Math::Max(int32, int32)")]
    [DataRow("int64 Math::Abs(int64)")]
    [DataRow("instance string Object::ToString()")]
    [DataRow("instance string String::Trim()")]
    [DataRow("string String::Concat(string, string)")]
    [DataRow("int32 Int32::Parse(string)")]
    [DataRow("instance int32 Int32::CompareTo(int32)")]
    [DataRow("!!0 [System.Linq]System.Linq.Enumerable::First<int32>(class IEnumerable`1<!!0>)")]
    [DataRow("Enumerable::Empty<string>()")]
    [DataRow("instance void class [System.Collections]System.Collections.Generic.List`1<int32>::Add(!0)")]
    [DataRow("instance void List<string>::Add(!0)")]
    [DataRow("instance bool Dictionary<string, int32>::TryGetValue(!0, !1&)")]
    [DataRow("instance !0 List<int32>::get_Item(int32)")]
    [DataRow("instance int32 List<int32>::get_Count()")]
    [DataRow("instance class [System.Runtime]System.Type Object::GetType()")]
    [DataRow("instance int32 StringBuilder::get_Length()")]
    [DataRow("instance class StringBuilder StringBuilder::Append(string)")]
    [DataRow("Enumerable::Select<[2]>(class IEnumerable`1<!!0>, class Func`2<!!0, !!1>)")]
    [DataRow("instance int32 Environment/SpecialFolder::GetHashCode()")]
    [DataRow("Array::Empty<int32>()")]
    [DataRow("string String::Join(string, string[])")]
    [DataRow("instance bool IEnumerator::MoveNext()")]
    [DataRow("instance void IDisposable::Dispose()")]
    public void BindMethod_RuntimeAndSnapshot_Agree(string text) => AssertSameMethod(Context, text);

    /// <summary>
    /// Constructors bind to the same member in both scopes.
    /// </summary>
    /// <param name="text">The reference.</param>
    [TestMethod]
    [DataRow("instance void StringBuilder::.ctor(int32)")]
    [DataRow("instance void [System.Runtime]System.Exception::.ctor(string)")]
    [DataRow("instance void List<int32>::.ctor()")]
    [DataRow("instance void Dictionary<string, int32>::.ctor(int32)")]
    public void BindConstructor_RuntimeAndSnapshot_Agree(string text) => AssertSameMethod(Context, text, wantConstructor: true);

    /// <summary>
    /// Field references bind to the same field in both scopes.
    /// </summary>
    /// <param name="text">The reference.</param>
    [TestMethod]
    [DataRow("string [System.Runtime]System.String::Empty")]
    [DataRow("String::Empty")]
    [DataRow("int32 Int32::MaxValue")]
    [DataRow("valuetype [System.Runtime]System.DateTime DateTime::MinValue")]
    [DataRow("float64 Math::PI")]
    public void BindField_RuntimeAndSnapshot_Agree(string text) => AssertSameField(Context, text);

    /// <summary>
    /// A resolver failure and a snapshot failure carry the same message.
    /// </summary>
    /// <param name="text">The reference.</param>
    [TestMethod]
    [DataRow("Console::WriteLine")]
    [DataRow("String::Split(char)")]
    [DataRow("String::Concta(string, string)")]
    [DataRow("instance void Object::.ctor(int32)")]
    public void BindMethod_Failures_Agree(string text)
    {
        var (runtime, snapshot, captured) = Scopes(Context);
        using (captured)
        {
            var syntax = CilSyntaxParser.ParseMethodReference(text);
            var fromRuntime = Assert.ThrowsExactly<ReplException>(() => SymbolBinder.BindMethodReference(syntax, runtime, text.Contains(
                ".ctor", StringComparison.Ordinal)));
            var fromSnapshot = Assert.ThrowsExactly<ReplException>(() => SymbolBinder.BindMethodReference(syntax, snapshot, text.Contains(
                ".ctor", StringComparison.Ordinal)));
            Assert.AreEqual(fromRuntime.Message.Split('\n')[0], fromSnapshot.Message.Split('\n')[0], text);
        }
    }

    /// <summary>
    /// A loaded assembly's members, vararg and generic ones included, bind alike.
    /// </summary>
    /// <param name="text">The reference.</param>
    [TestMethod]
    [DataRow("string Greeter.Hello::Say(string)")]
    [DataRow("Greeter.Hello::Add(int32, int32)")]
    [DataRow("int64 Greeter.Hello::Add(int64, int64)")]
    [DataRow("vararg int32 Greeter.Hello::CountArgs(..., int32, string)")]
    [DataRow("!!0 Greeter.Hello::Echo<string>(!!0)")]
    [DataRow("[Greeter]Greeter.Hello::Sum(int32[])")]
    public void BindMethod_LoadedAssembly_Agrees(string text) => AssertSameMethod(ContextWithGreeter(), text);

    /// <summary>
    /// A loaded assembly's types and fields bind alike, through the hint and through the short name.
    /// </summary>
    [TestMethod]
    public void BindType_LoadedAssembly_Agrees()
    {
        var context = ContextWithGreeter();
        AssertSameType(context, "Greeter.Hello");
        AssertSameType(context, "[Greeter]Greeter.Hello");
        AssertSameType(context, "class Greeter.Counter");
        AssertSameField(context, "string Greeter.Hello::Prefix");
        AssertSameField(context, "Greeter.Hello::Calls");
    }

    /// <summary>
    /// A session's accepted types and their members bind alike from the cell.
    /// </summary>
    [TestMethod]
    public void Bind_SessionTypes_Agree()
    {
        var session = IlLines.Load(
            ".class public Base {",
            ".field public int32 Pub",
            ".field public static int32 SPub",
            ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }",
            ".method public instance int32 Get() { ldc.i4 1; ret }",
            ".method public static int32 SPubM() { ldc.i4 5; ret }",
            "}",
            ".class public Derived extends Base {",
            ".method public instance void .ctor() { ldarg.0; call instance void Base::.ctor(); ret }",
            "}",
            ".class public Box`1<T> {",
            ".field public !0 Value",
            ".method public instance !0 Get() { ldarg.0; ldfld !0 class Box`1<!0>::Value; ret }",
            "}",
            ".method public static int32 Fib(int32 n) { ldarg.0; ret }");
        var context = session.State.Context;
        AssertSameType(context, "Base");
        AssertSameType(context, "Derived");
        AssertSameType(context, "class Box`1<int32>");
        AssertSameType(context, "Box<string>");
        AssertSameMethod(context, "instance int32 Base::Get()");
        AssertSameMethod(context, "instance int32 Derived::Get()");
        AssertSameMethod(context, "Base::SPubM()");
        AssertSameMethod(context, "instance void Derived::.ctor()", wantConstructor: true);
        AssertSameMethod(context, "instance !0 class Box`1<int32>::Get()");
        AssertSameMethod(context, "Fib(int32)");
        AssertSameMethod(context, "int32 Fib(int32)");
        AssertSameField(context, "int32 Base::Pub");
        AssertSameField(context, "Derived::Pub");
        AssertSameField(context, "!0 class Box`1<string>::Value");
    }

    /// <summary>
    /// Checks declared, inherited, generic, and forward member binding without mutating the live block.
    /// </summary>
    /// <remarks>
    /// Inside a class being written, the declared members, the base's members, and the type's own
    /// parameters bind alike, and a member declared ahead of its line is recorded on both sides
    /// without the snapshot touching the real block.
    /// </remarks>
    [TestMethod]
    public void Bind_OpenClass_Agrees()
    {
        var session = IlLines.Load(
            ".class public Base {",
            ".field family int32 Fam",
            ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }",
            ".method family instance int32 FamM() { ldc.i4 1; ret }",
            "}");
        session.AddLine(".class public Own`1<T> extends Base {");
        session.AddLine(".field private !0 Item");
        session.AddLine(".method public instance !0 Get() {");
        var context = session.State.Context;
        AssertSameType(context, "Own`1");
        AssertSameType(context, "class Own`1<!0>");
        AssertSameType(context, "!0");
        AssertSameType(context, "!T");
        AssertSameField(context, "!0 class Own`1<!0>::Item");
        AssertSameField(context, "int32 Own`1::Fam");
        AssertSameMethod(context, "instance !0 class Own`1<!0>::Get()");
        AssertSameMethod(context, "instance int32 class Own`1<!0>::FamM()");

        var (runtime, snapshot, captured) = Scopes(context);
        using (captured)
        {
            var before = session.State.Types.TryGetMembers(session.State.Types.Types.Last(t => t.Name.StartsWith("Own",
                StringComparison.Ordinal)), out var own) ? own.Methods.Count : -1;
            var syntax = CilSyntaxParser.ParseMethodReference("void Own`1::Later(int32)");
            var fromSnapshot = SymbolBinder.BindMethodReference(syntax, snapshot, false);
            Assert.AreEqual(MethodSymbolSource.Forward, fromSnapshot.Method.Source);
            Assert.IsFalse(fromSnapshot.Method.IsDeclared);
            Assert.HasCount(before, own!.Methods, "the snapshot declared nothing on the real block");
            var fromRuntime = SymbolBinder.BindMethodReference(syntax, runtime, false);
            Assert.AreEqual(MethodSymbolSource.Forward, fromRuntime.Method.Source);
            Assert.HasCount(before + 1, own.Methods, "the runtime scope declares ahead, as the resolver does");
        }
    }

    /// <summary>
    /// The snapshot's short-name search agrees with the resolver's over a sample of framework types.
    /// </summary>
    [TestMethod]
    public void LookupType_ShortNames_AgreeWithTheResolver()
    {
        var (runtime, snapshot, captured) = Scopes(Context);
        using (captured)
        {
            var names = new[]
            {
                "Console", "StringBuilder", "List`1", "Dictionary`2", "Task", "Task`1", "Regex", "Stopwatch", "Path", "File", "Math",
                    "Random",
                "Guid", "DateTime", "TimeSpan", "Uri", "Encoding", "Stream", "MemoryStream", "Exception", "ArgumentException",
                    "IDisposable",
                "IEnumerable`1", "IComparable`1", "Func`2", "Action`1", "Nullable`1", "ValueTuple`2", "BigInteger", "Complex", "Thread",
                "CancellationToken", "Interlocked", "GC", "Type", "MethodInfo", "Attribute", "Enum", "ValueType", "Delegate", "Array",
                    "Span`1",
                "Memory`1", "ImmutableArray`1", "ImmutableList`1", "ConcurrentDictionary`2", "WebUtility", "JsonSerializer", "Process",
                    "Environment",
                "RuntimeHelpers", "Marshal", "Vector`1", "Half", "Int128", "Index", "Range", "Lazy`1", "WeakReference`1", "KeyValuePair`2",
            };
            var agreed = 0;
            foreach (var name in names)
            {
                TypeSymbol? fromRuntime = null;
                TypeSymbol? fromSnapshot = null;
                string? runtimeError = null;
                string? snapshotError = null;
                try
                {
                    fromRuntime = runtime.LookupType(name, null, 0, false).Type;
                }
                catch (ReplException ex)
                {
                    runtimeError = ex.Message;
                }

                try
                {
                    fromSnapshot = snapshot.LookupType(name, null, 0, false).Type;
                }
                catch (ReplException ex)
                {
                    snapshotError = ex.Message;
                }

                Assert.AreEqual(runtimeError is null, snapshotError is null, name + ": " + (runtimeError ?? snapshotError));
                if (fromRuntime is not null)
                {
                    Assert.AreEqual(fromRuntime, fromSnapshot, name);
                    agreed++;
                }
                else
                {
                    Assert.AreEqual(runtimeError!.Split(':')[0], snapshotError!.Split(':')[0], name);
                }
            }

            Assert.IsGreaterThan(40, agreed);
        }
    }

    private static ParseContext ContextWithGreeter()
    {
        var resolver = new TypeResolver();
        resolver.Load(SampleHost.Samples.GreeterDll);
        return new ParseContext([], [], GenericContext.Empty, resolver, []);
    }
}
