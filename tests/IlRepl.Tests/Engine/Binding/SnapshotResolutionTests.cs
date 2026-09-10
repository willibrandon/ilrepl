using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using Mono.Cecil;
using MA = Mono.Cecil.MethodAttributes;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Checks metadata-only resolution, exact forwarding, load-context identity, and missing dependencies.
/// </summary>
/// <remarks>
/// A snapshot answers from metadata alone: binding against it loads nothing, raises no resolution
/// event, follows facade forwarders to the defining assembly, keeps references inside the load
/// context that made them, and leaves a reference nothing defines unresolved without dropping
/// the healthy members beside it.
/// </remarks>
[TestClass]
public sealed class SnapshotResolutionTests
{
    /// <summary>
    /// Capturing and binding against a snapshot raise no load or resolution event, whatever is referenced.
    /// </summary>
    [TestMethod]
    public void Snapshot_BindingRaisesNoLoadOrResolutionEvent()
    {
        var context = new ParseContext([], [], GenericContext.Empty, new TypeResolver(), []);
        // Other tests run in parallel and load what they like; only this thread's events count.
        var thread = Environment.CurrentManagedThreadId;
        var events = new List<string>();
        void Record(string text)
        {
            if (Environment.CurrentManagedThreadId == thread)
            {
                events.Add(text);
            }
        }

        AssemblyLoadEventHandler loaded = (_, e) => Record("AssemblyLoad " + e.LoadedAssembly.GetName().Name);
        ResolveEventHandler assemblyResolve = (_, e) => { Record("AssemblyResolve " + e.Name); return null; };
        ResolveEventHandler typeResolve = (_, e) => { Record("TypeResolve " + e.Name); return null; };
        Func<AssemblyLoadContext, AssemblyName, Assembly?> resolving = (_, name) => { Record("Resolving " + name.Name); return null; };
        AppDomain.CurrentDomain.AssemblyLoad += loaded;
        AppDomain.CurrentDomain.AssemblyResolve += assemblyResolve;
        AppDomain.CurrentDomain.TypeResolve += typeResolve;
        AssemblyLoadContext.Default.Resolving += resolving;
        try
        {
            using var snapshot = BindingSnapshot.Capture(context);
            var scope = new SnapshotBindingScope(snapshot);
            foreach (var text in new[] { "[System.Runtime]System.String",
                "class [System.Collections]System.Collections.Generic.List`1<int32>", "Console", "Dictionary<string, int32>" })
            {
                SymbolBinder.BindType(CilSyntaxParser.ParseType(text), scope);
            }

            SymbolBinder.BindMethodReference(CilSyntaxParser.ParseMethodReference("void [System.Console]System.Console::WriteLine(string)"),
                scope, false);
            SymbolBinder.BindMethodReference(CilSyntaxParser.ParseMethodReference("!!0 Enumerable::First<int32>(class IEnumerable`1<!!0>)"),
                scope, false);
            Assert.ThrowsExactly<ReplException>(() => SymbolBinder.BindType(CilSyntaxParser.ParseType("[Unknown.Assembly]Some.Type"),
                scope));
            Assert.ThrowsExactly<ReplException>(() => SymbolBinder.BindType(CilSyntaxParser.ParseType("NoSuchTypeAnywhere"), scope));
            Assert.ThrowsExactly<ReplException>(() => SymbolBinder.BindType(CilSyntaxParser.ParseType("Enumerator"), scope));
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= loaded;
            AppDomain.CurrentDomain.AssemblyResolve -= assemblyResolve;
            AppDomain.CurrentDomain.TypeResolve -= typeResolve;
            AssemblyLoadContext.Default.Resolving -= resolving;
        }

        Assert.IsEmpty(events, string.Join("; ", events));
    }

    /// <summary>
    /// Checks facade references resolve to the same core definition as runtime lookup.
    /// </summary>
    /// <remarks>
    /// A reference through a facade lands on the definition in the core library, by the same
    /// identity a runtime type has.
    /// </remarks>
    [TestMethod]
    public void Snapshot_FollowsFacadeForwarders()
    {
        var context = new ParseContext([], [], GenericContext.Empty, new TypeResolver(), []);
        using var snapshot = BindingSnapshot.Capture(context);
        var scope = new SnapshotBindingScope(snapshot);
        var viaRuntime = SymbolBinder.BindType(CilSyntaxParser.ParseType("[System.Runtime]System.Text.StringBuilder"), scope).Type;
        Assert.AreEqual(RuntimeSymbolImporter.Import(typeof(System.Text.StringBuilder)), viaRuntime);
        Assert.AreEqual("System.Private.CoreLib", viaRuntime.AssemblyName);

        var list = SymbolBinder.BindType(CilSyntaxParser.ParseType("class [System.Collections]System.Collections.Generic.List`1<int32>"),
            scope).Type;
        Assert.AreEqual(RuntimeSymbolImporter.Import(typeof(List<int>)), list);

        var runtimeSource = snapshot.Catalog.FindAssembly("System.Runtime");
        Assert.IsNotNull(runtimeSource);
        Assert.IsTrue(runtimeSource.Index.TryGetForwarder("System", "String", out _), "System.Runtime forwards String");
        Assert.IsNotNull(snapshot.Catalog.FindType(runtimeSource, "System", "String"));
    }

    /// <summary>
    /// Keeps unresolved signatures without withholding neighboring healthy members.
    /// </summary>
    /// <remarks>
    /// A member whose signature names a type no loaded assembly defines is kept with an unresolved
    /// part, and the healthy members beside it bind and confirm.
    /// </remarks>
    [TestMethod]
    public void Snapshot_MissingDependency_IsolatesTheAffectedMember()
    {
        var resolver = new TypeResolver();
        var (assembly, _, fixture) = CecilFixture.Build(
            (module, type) =>
            {
                var missing = new AssemblyNameReference("IlRepl.Missing.Dependency", new Version(1, 0, 0, 0));
                module.AssemblyReferences.Add(missing);
                var gone = new TypeReference("Missing", "Gone", module, missing);
                var broken = new MethodDefinition("Broken", MA.Public | MA.Static, module.TypeSystem.Void);
                broken.Parameters.Add(new ParameterDefinition("g", Mono.Cecil.ParameterAttributes.None, gone));
                broken.Body.GetILProcessor().Emit(Mono.Cecil.Cil.OpCodes.Ret);
                type.Methods.Add(broken);
                var healthy = new MethodDefinition("Healthy", MA.Public | MA.Static, module.TypeSystem.Int32);
                healthy.Parameters.Add(new ParameterDefinition("x", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
                var il = healthy.Body.GetILProcessor();
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                type.Methods.Add(healthy);
            },
            resolver);
        var context = new ParseContext([], [], GenericContext.Empty, resolver, []);
        var thread = Environment.CurrentManagedThreadId;
        var events = new List<string>();
        ResolveEventHandler assemblyResolve = (_, e) =>
        {
            if (Environment.CurrentManagedThreadId == thread)
            {
                events.Add(e.Name);
            }

            return null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += assemblyResolve;
        try
        {
            using var snapshot = BindingSnapshot.Capture(context);
            var scope = new SnapshotBindingScope(snapshot);
            var declaring = SymbolBinder.BindType(CilSyntaxParser.ParseType("N.Fixture"), scope).Type;
            Assert.AreEqual(RuntimeSymbolImporter.Import(fixture), declaring);
            var methods = scope.Methods(declaring, "Broken");
            Assert.HasCount(1, methods);
            Assert.IsTrue(methods[0].Parameters[0].Type.HasUnresolved);
            Assert.AreEqual(TypeSymbolKind.Unresolved, methods[0].Parameters[0].Type.Kind);
            Assert.AreEqual("Gone", methods[0].Parameters[0].Type.Name);
            Assert.AreEqual("IlRepl.Missing.Dependency", methods[0].Parameters[0].Type.AssemblyName);

            var healthy = SymbolBinder.BindMethodReference(CilSyntaxParser.ParseMethodReference("N.Fixture::Healthy(int32)"), scope, false);
            Assert.AreEqual(RuntimeSymbolImporter.Import(fixture.GetMethod("Healthy")!), healthy.Method);
            Assert.IsTrue(healthy.Method.Parameters.All(p => !p.Type.HasUnresolved));
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= assemblyResolve;
        }

        Assert.IsEmpty(events, "no resolution event was raised for the missing dependency");
        GC.KeepAlive(assembly);
    }

    /// <summary>
    /// Resolves identical dependency names to the copy in the requesting load context.
    /// </summary>
    /// <remarks>
    /// The same dependency loaded into two contexts resolves to the copy in the requester's own
    /// context, never to the other by a matching name or token.
    /// </remarks>
    [TestMethod]
    public void Snapshot_ReferencesStayInTheirLoadContext()
    {
        var dependencyName = "IlRepl.Parity.Dependency" + Guid.NewGuid().ToString("N");
        byte[] dependencyImage;
        using (var definition = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(dependencyName, new Version(1, 0, 0, 0)),
            dependencyName, ModuleKind.Dll))
        {
            var module = definition.MainModule;
            module.Types.Add(new TypeDefinition("D", "Shared", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.TypeSystem.Object));
            using var stream = new MemoryStream();
            definition.Write(stream);
            dependencyImage = stream.ToArray();
        }

        var userName = "IlRepl.Parity.User" + Guid.NewGuid().ToString("N");
        byte[] userImage;
        using (var definition = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(userName, new Version(1, 0, 0, 0)), userName,
            ModuleKind.Dll))
        {
            var module = definition.MainModule;
            var reference = new AssemblyNameReference(dependencyName, new Version(1, 0, 0, 0));
            module.AssemblyReferences.Add(reference);
            var shared = new TypeReference("D", "Shared", module, reference);
            var type = new TypeDefinition("U", "Holder", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.TypeSystem.Object);
            type.Fields.Add(new FieldDefinition("Value", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, shared));
            module.Types.Add(type);
            using var stream = new MemoryStream();
            definition.Write(stream);
            userImage = stream.ToArray();
        }

        var first = new AssemblyLoadContext("parity-first", isCollectible: true);
        var second = new AssemblyLoadContext("parity-second", isCollectible: true);
        try
        {
            var dependencyA = first.LoadFromStream(new MemoryStream(dependencyImage));
            var userA = first.LoadFromStream(new MemoryStream(userImage));
            var dependencyB = second.LoadFromStream(new MemoryStream(dependencyImage));
            var userB = second.LoadFromStream(new MemoryStream(userImage));
            var sources = new[] { userA, dependencyB, userB, dependencyA }.Select(a => (a, AssemblySymbolSource.For(a)!)).ToList();
            var catalog = new LoadedBindingCatalog(sources);
            var holderA = catalog.FindType(sources[0].Item2, "U", "Holder")!;
            var holderB = catalog.FindType(sources[2].Item2, "U", "Holder")!;
            var fieldA = sources[0].Item2.Fields(sources[0].Item2.TypeHandleOf(holderA.Definition)!.Value, catalog)[0];
            var fieldB = sources[2].Item2.Fields(sources[2].Item2.TypeHandleOf(holderB.Definition)!.Value, catalog)[0];
            Assert.AreEqual(RuntimeDefinitions.AssemblyInstance(dependencyA), fieldA.FieldType.Definition.Assembly,
                "Holder in the first context sees the first context's dependency");
            Assert.AreEqual(RuntimeDefinitions.AssemblyInstance(dependencyB), fieldB.FieldType.Definition.Assembly,
                "Holder in the second context sees the second context's dependency");
            Assert.AreEqual(fieldA.FieldType.Definition.Token, fieldB.FieldType.Definition.Token);
            Assert.AreNotEqual(fieldA.FieldType, fieldB.FieldType);
            Assert.AreEqual(RuntimeSymbolImporter.Import(dependencyA.GetType("D.Shared")!), fieldA.FieldType);
        }
        finally
        {
            first.Unload();
            second.Unload();
        }
    }

    /// <summary>
    /// A reference to an assembly the catalog holds twice in one context, or not at all, stays unresolved.
    /// </summary>
    [TestMethod]
    public void Snapshot_AmbiguousOrAbsentReference_StaysUnresolved()
    {
        var context = new ParseContext([], [], GenericContext.Empty, new TypeResolver(), []);
        using var snapshot = BindingSnapshot.Capture(context);
        var (_, _, fixture) = CecilFixture.Build(
            (module, type) =>
            {
                var missing = new AssemblyNameReference("IlRepl.Absent", new Version(1, 0, 0, 0));
                module.AssemblyReferences.Add(missing);
                type.Fields.Add(new FieldDefinition("Absent", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
                    new TypeReference("A", "B", module, missing)));
            });
        var source = AssemblySymbolSource.For(fixture.Assembly)!;
        var catalog = new LoadedBindingCatalog([(fixture.Assembly, source)]);
        var handle = source.TypeHandleOf(RuntimeSymbolImporter.Import(fixture).Definition)!.Value;
        var field = source.Fields(handle, catalog)[0];
        Assert.AreEqual(TypeSymbolKind.Unresolved, field.FieldType.Kind);
        Assert.IsTrue(field.FieldType.HasUnresolved);
        var again = source.Fields(handle, catalog)[0];
        Assert.IsFalse(SymbolIdentity.Equal(field.FieldType, again.FieldType),
            "two readings of a reference nothing defines are not one type");
    }

    /// <summary>
    /// The metadata source gives the same identity and facts as the runtime importer for the same definition.
    /// </summary>
    [TestMethod]
    public void Source_DefinitionFacts_MatchTheRuntimeImporter()
    {
        var context = new ParseContext([], [], GenericContext.Empty, new TypeResolver(), []);
        using var snapshot = BindingSnapshot.Capture(context);
        foreach (var type in new[] { typeof(List<>), typeof(Dictionary<,>.Enumerator), typeof(Environment.SpecialFolder), typeof(Action<>),
            typeof(IComparable<>), typeof(ValueTuple<,>), typeof(Enum), typeof(ValueType), typeof(System.Text.StringBuilder) })
        {
            var expected = RuntimeSymbolImporter.Import(type);
            var located = snapshot.Catalog.Locate(expected);
            Assert.IsNotNull(located, type.Name);
            var actual = located.Value.Source.Definition(located.Value.Handle);
            Assert.AreEqual(expected, actual, type.Name);
            Assert.AreEqual(expected.Name, actual.Name, type.Name);
            Assert.AreEqual(expected.Namespace, actual.Namespace, type.Name);
            Assert.AreEqual(expected.Attributes, actual.Attributes, type.Name);
            Assert.AreEqual(expected.IsValueType, actual.IsValueType, type.Name);
            Assert.AreSequenceEqual(expected.GenericParameterNames, actual.GenericParameterNames, type.Name);
            Assert.AreEqual(expected.Declaring, actual.Declaring, type.Name);
        }
    }
}
