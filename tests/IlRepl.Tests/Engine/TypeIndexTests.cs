using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="TypeIndex"/>: one index over the session's types and every loaded
/// assembly, built from metadata, that agrees with the resolver on what a short name means.
/// </summary>
[TestClass]
public sealed class TypeIndexTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver(), []);

    /// <summary>
    /// For a sample of short names, the index finds what the resolver finds, or fails where it fails.
    /// </summary>
    [TestMethod]
    public void TypeIndex_ShortNameTarget_AgreesWithResolve()
    {
        using var snapshot = BindingSnapshot.Capture(Context);
        Assert.Contains(s => s.Name == "System.Reflection.Metadata", snapshot.SearchOrder, "the reader's own assembly, loaded while the snapshot was taken, is searched too");
        var index = new TypeIndex(snapshot);
        var sampled = index.Entries
            .Where(e => e.IsVisible && !e.IsNested && !e.IsCompilerGenerated)
            .Select(e => e.Name)
            .Distinct(StringComparer.Ordinal)
            .Where((_, i) => i % 23 == 0)
            .Take(200)
            .ToList();
        Assert.IsGreaterThanOrEqualTo(150, sampled.Count);
        var disagreements = new List<string>();
        foreach (var name in sampled)
        {
            TypeSymbol? expected = null;
            try
            {
                expected = RuntimeSymbolImporter.Import(Context.Resolver.Resolve(name, null));
            }
            catch (ReplException)
            {
                // Ambiguous or not found through the resolver; the index must agree.
            }

            var actual = index.ShortNameTarget(name);
            if (!SymbolIdentity.Equal(expected, actual) && !(expected is null && actual is null))
            {
                var entries = string.Join(" | ", index.BySimpleName(name).Select(e => $"{e.IlPath} [{e.AssemblyName}] visible={e.IsVisible}"));
                disagreements.Add($"{name}: resolver {(expected is null ? "none" : SymbolRenderer.IlPath(expected))}, index {(actual is null ? "none" : SymbolRenderer.IlPath(actual))}; entries {entries}");
            }
        }

        Assert.IsEmpty(disagreements, string.Join("\n", disagreements));
    }

    /// <summary>
    /// A snapshot taken after a load indexes the loaded assembly's types.
    /// </summary>
    [TestMethod]
    public void TypeIndex_Rebuilds_WhenAnAssemblyIsLoaded()
    {
        var resolver = new TypeResolver();
        var context = new ParseContext([], [], GenericContext.Empty, resolver, []);
        var name = "Fresh" + Guid.NewGuid().ToString("N");
        using (var before = BindingSnapshot.Capture(context))
        {
            Assert.IsEmpty(new TypeIndex(before).ByFullName("Later." + name));
        }

        var (assembly, _, _) = CecilFixture.Build(
            (module, _) => module.Types.Add(new TypeDefinition("Later", name, Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object)),
            resolver);
        using var after = BindingSnapshot.Capture(context);
        var index = new TypeIndex(after);
        var fresh = index.ByFullName("Later." + name);
        Assert.HasCount(1, fresh);
        Assert.AreEqual(assembly.GetName().Name, fresh[0].AssemblyName);
        Assert.AreEqual(TypeIndexKind.Class, fresh[0].Kind);

        resolver.Load(SampleHost.Samples.GreeterDll);
        using var withGreeter = BindingSnapshot.Capture(context);
        var greeter = new TypeIndex(withGreeter);
        Assert.IsTrue(greeter.ByFullName("Greeter.Outer/Inner").Single().IsNested);
        Assert.AreEqual(TypeIndexKind.Interface, greeter.ByFullName("Greeter.IParse`1").Single().Kind);
        Assert.AreEqual(1, greeter.ByFullName("Greeter.IParse`1").Single().Arity);
        Assert.AreEqual(TypeIndexKind.Struct, greeter.ByFullName("Greeter.Number").Single().Kind);
        Assert.HasCount(2, greeter.BySimpleName("Counter").Where(e => e.AssemblyName == "Greeter").ToList());
    }

    /// <summary>
    /// An assembly whose dependency is absent is indexed whole; only the members that need the
    /// dependency carry an unresolved part.
    /// </summary>
    [TestMethod]
    public void TypeIndex_SurvivesAMissingDependency()
    {
        var resolver = new TypeResolver();
        var (_, _, fixture) = CecilFixture.Build(
            (module, type) =>
            {
                var missing = new AssemblyNameReference("IlRepl.Index.Missing", new Version(1, 0, 0, 0));
                module.AssemblyReferences.Add(missing);
                var gone = new TypeReference("Missing", "Gone", module, missing);
                type.Fields.Add(new FieldDefinition("Broken", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, gone));
                type.Fields.Add(new FieldDefinition("Healthy", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Int32));
                module.Types.Add(new TypeDefinition("N", "Sibling", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object));
                module.Types.Add(new TypeDefinition("N", "Dependent", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, gone));
            },
            resolver);
        var context = new ParseContext([], [], GenericContext.Empty, resolver, []);
        using var snapshot = BindingSnapshot.Capture(context);
        var index = new TypeIndex(snapshot);
        var assemblyName = fixture.Assembly.GetName().Name;
        Assert.HasCount(1, index.ByFullName("N.Fixture").Where(e => e.AssemblyName == assemblyName).ToList());
        Assert.HasCount(1, index.ByFullName("N.Sibling").Where(e => e.AssemblyName == assemblyName).ToList());
        Assert.HasCount(1, index.ByFullName("N.Dependent").Where(e => e.AssemblyName == assemblyName).ToList());
        var scope = new SnapshotBindingScope(snapshot);
        var declaring = RuntimeSymbolImporter.Import(fixture);
        var fields = scope.Fields(declaring);
        Assert.IsTrue(fields.Single(f => f.Name == "Broken").FieldType.HasUnresolved);
        Assert.IsFalse(fields.Single(f => f.Name == "Healthy").FieldType.HasUnresolved);
        var dependentBase = scope.BaseOf(index.SymbolOf(index.ByFullName("N.Dependent").Single(e => e.AssemblyName == assemblyName))!);
        Assert.IsNotNull(dependentBase);
        Assert.AreEqual(TypeSymbolKind.Unresolved, dependentBase.Kind, "a base nothing defines stays an unresolved spelling");
        Assert.IsNull(scope.BaseOf(dependentBase), "and the chain ends there");
    }

    /// <summary>
    /// Every definition appears once, whatever forwards it.
    /// </summary>
    [TestMethod]
    public void TypeIndex_HoldsOneEntryPerDefinition()
    {
        using var snapshot = BindingSnapshot.Capture(Context);
        var index = new TypeIndex(snapshot);
        var duplicates = index.Entries.GroupBy(e => e.Definition).Where(g => g.Count() > 1).Select(g => g.Key.ToString()).ToList();
        Assert.IsEmpty(duplicates, string.Join(", ", duplicates.Take(5)));
        Assert.HasCount(1, index.ByFullName("System.String"));
        Assert.HasCount(1, index.ByFullName("System.Collections.Generic.List`1"));
        Assert.AreEqual(TypeIndexKind.Primitive, index.ByFullName("System.Int32").Single().Kind);
    }

    /// <summary>
    /// Non-public top-level types are indexed too: a listing may name them, even if a cell cannot.
    /// </summary>
    [TestMethod]
    public void TypeIndex_HoldsNonPublicTopLevelTypes()
    {
        var resolver = new TypeResolver();
        CecilFixture.Build(
            (module, _) => module.Types.Add(new TypeDefinition("N", "Hidden", Mono.Cecil.TypeAttributes.NotPublic | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object)),
            resolver);
        var context = new ParseContext([], [], GenericContext.Empty, resolver, []);
        using var snapshot = BindingSnapshot.Capture(context);
        var index = new TypeIndex(snapshot);
        var hidden = index.ByFullName("N.Hidden").Single();
        Assert.IsFalse(hidden.IsVisible);
        Assert.IsNull(index.ShortNameTarget("Hidden"), "the resolver's scan sees exported types only");
        Assert.IsNotNull(index.SymbolOf(hidden));
    }

    /// <summary>
    /// The index is built from metadata: no assembly is loaded and no resolution event is raised.
    /// </summary>
    [TestMethod]
    public void TypeIndex_DiscoversFromMetadataWithoutMaterializing()
    {
        // The first index in a process brings in the engine's own dependencies, which is the
        // runtime loading the engine, not the index loading a user's assembly; the steady state is judged.
        using (var warm = BindingSnapshot.Capture(Context))
        {
            _ = new TypeIndex(warm).ShortNameTarget("StringBuilder");
        }

        var thread = Environment.CurrentManagedThreadId;
        var events = new List<string>();
        AssemblyLoadEventHandler loaded = (_, e) =>
        {
            if (Environment.CurrentManagedThreadId == thread)
            {
                events.Add("load " + e.LoadedAssembly.GetName().Name);
            }
        };
        ResolveEventHandler resolve = (_, e) =>
        {
            if (Environment.CurrentManagedThreadId == thread)
            {
                events.Add("resolve " + e.Name);
            }

            return null;
        };
        AppDomain.CurrentDomain.AssemblyLoad += loaded;
        AppDomain.CurrentDomain.TypeResolve += resolve;
        AppDomain.CurrentDomain.AssemblyResolve += resolve;
        try
        {
            using var snapshot = BindingSnapshot.Capture(Context);
            var index = new TypeIndex(snapshot);
            Assert.IsGreaterThan(2000, index.Entries.Count);
            _ = index.ShortNameTarget("StringBuilder");
            _ = index.ByPath("System.Runtime", null, null);
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= loaded;
            AppDomain.CurrentDomain.TypeResolve -= resolve;
            AppDomain.CurrentDomain.AssemblyResolve -= resolve;
        }

        Assert.IsEmpty(events, string.Join("; ", events));
    }

    /// <summary>
    /// A type initializer is among the members metadata lists, and a throwing one is never run.
    /// </summary>
    [TestMethod]
    public void MetadataMembers_IncludesTheTypeInitializer()
    {
        var resolver = new TypeResolver();
        resolver.Load(SampleHost.Samples.GreeterDll);
        var context = new ParseContext([], [], GenericContext.Empty, resolver, []);
        using var snapshot = BindingSnapshot.Capture(context);
        var scope = new SnapshotBindingScope(snapshot);
        var trap = SymbolBinder.BindType(CilSyntaxParser.ParseType("Greeter.Trap"), scope).Type;
        var initializer = scope.Constructors(trap, isStatic: true);
        Assert.HasCount(1, initializer);
        Assert.AreEqual(".cctor", initializer[0].Name);
        Assert.IsTrue(initializer[0].IsStatic);
        Assert.IsEmpty(scope.Constructors(trap, isStatic: false));
        var value = scope.Methods(trap, "get_Value");
        Assert.HasCount(1, value);
        Assert.IsTrue(value[0].IsStatic);
    }

    /// <summary>
    /// Healthy rows of a type bind even when a sibling row needs an absent assembly.
    /// </summary>
    [TestMethod]
    public void MetadataMembers_HealthyRowsSurviveUnrelatedMissingDependencies()
    {
        var resolver = new TypeResolver();
        var (_, _, fixture) = CecilFixture.Build(
            (module, type) =>
            {
                var missing = new AssemblyNameReference("IlRepl.Members.Missing", new Version(1, 0, 0, 0));
                module.AssemblyReferences.Add(missing);
                var gone = new TypeReference("Missing", "Gone", module, missing);
                var broken = new MethodDefinition("Broken", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, gone);
                broken.Body.GetILProcessor().Emit(Mono.Cecil.Cil.OpCodes.Ret);
                type.Methods.Add(broken);
                var healthy = new MethodDefinition("Healthy", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
                var il = healthy.Body.GetILProcessor();
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_7);
                il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                type.Methods.Add(healthy);
            },
            resolver);
        var context = new ParseContext([], [], GenericContext.Empty, resolver, []);
        var resolved = MemberResolver.ResolveMethod("N.Fixture::Healthy()", context, false);
        Assert.AreEqual(fixture.GetMethod("Healthy"), resolved.Method);
        using var snapshot = BindingSnapshot.Capture(context);
        var scope = new SnapshotBindingScope(snapshot);
        var bound = SymbolBinder.BindMethodReference(CilSyntaxParser.ParseMethodReference("N.Fixture::Healthy()"), scope, false);
        Assert.AreEqual(RuntimeSymbolImporter.Import(fixture.GetMethod("Healthy")!), bound.Method);
        Assert.IsTrue(scope.Methods(RuntimeSymbolImporter.Import(fixture), "Broken").Single().ReturnType.HasUnresolved);
    }

    /// <summary>
    /// Two contexts holding the same bytes are two sets of entries with distinct identities.
    /// </summary>
    [TestMethod]
    public void TypeIndex_DistinguishesLoadsOfTheSameBytes()
    {
        var (_, image, _) = CecilFixture.Build((_, _) => { });
        var first = new AssemblyLoadContext("index-first", isCollectible: true);
        var second = new AssemblyLoadContext("index-second", isCollectible: true);
        try
        {
            var a = first.LoadFromStream(new MemoryStream(image));
            var b = second.LoadFromStream(new MemoryStream(image));
            var sourceA = AssemblySymbolSource.For(a)!;
            var sourceB = AssemblySymbolSource.For(b)!;
            var entryA = sourceA.Index.Entries.Single(e => e.Name == "Fixture");
            var entryB = sourceB.Index.Entries.Single(e => e.Name == "Fixture");
            Assert.AreEqual(entryA.Definition.Token, entryB.Definition.Token);
            Assert.AreNotEqual(entryA.Definition, entryB.Definition);
        }
        finally
        {
            first.Unload();
            second.Unload();
        }
    }
}
