using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for how session assemblies are named, loaded, recognized, kept alive, and released.
/// Each fact here was first established by a stage 1 spike against the runtime.
/// </summary>
[TestClass]
public sealed partial class DefinitionAssemblyTests
{
    /// <summary>
    /// Names are unique across kinds and the version never carries the counter.
    /// </summary>
    [TestMethod]
    public void NextName_IsUniqueAndVersionIsFixed()
    {
        var a = SessionAssemblies.NextName(SessionAssemblyKind.Types);
        var b = SessionAssemblies.NextName(SessionAssemblyKind.Types);
        var c = SessionAssemblies.NextName(SessionAssemblyKind.Cell);
        Assert.AreNotEqual(a, b);
        Assert.AreNotEqual(b, c);
        Assert.StartsWith("ilrepl.types.", a);
        Assert.StartsWith("ilrepl.cell.", c);
        Assert.AreEqual(new Version(1, 0, 0, 0), SessionAssemblies.MakeAssemblyName(a).Version);
        Assert.IsTrue(SessionAssemblies.IsSessionName(a));
        Assert.IsFalse(SessionAssemblies.IsSessionName("System.Runtime"));
    }

    /// <summary>
    /// A definition calling another resolves through the registry by exact name, its
    /// <c>assembly</c> member is reachable through the access-check attribute, the loaded
    /// assembly object is what the registry recognizes, and an attribute naming a type in the
    /// other assembly resolves from the defining module.
    /// </summary>
    [TestMethod]
    public void Load_CrossDefinitionReference_ResolvesThroughRegistry()
    {
        var point = Images.LoadPoint();
        var holder = Images.LoadHolder(point);
        var holderType = holder.Assembly.GetType("Holder")!;
        Assert.AreEqual(16, holderType.GetMethod("Read")!.Invoke(null, null));
        Assert.IsTrue(SessionAssemblies.IsSessionAssembly(holderType.Assembly));
        Assert.IsTrue(SessionAssemblies.TryGetDefinition(holderType.Assembly, out var definition));
        Assert.AreSame(point, definition!.Dependencies[0]);
        var pointType = point.Assembly.GetType("Point")!;
        Assert.IsTrue(SessionAssemblies.IsSessionType(pointType));
        Assert.IsTrue(SessionAssemblies.IsSessionInstance(Activator.CreateInstance(pointType)));
        Assert.IsFalse(SessionAssemblies.IsSessionInstance(Array.CreateInstance(pointType, 1)));
        Assert.IsFalse(SessionAssemblies.IsSessionInstance(new object()));
        Assert.IsFalse(SessionAssemblies.IsSessionType(typeof(string)));
        var argument = (Type)holderType.GetCustomAttributesData().Single().ConstructorArguments[0].Value!;
        Assert.AreSame(pointType, argument);
    }

    /// <summary>
    /// A retained type keeps its unresolved dependency alive after the session releases both,
    /// first use still works, and everything collects once the type is dropped. Not parallel:
    /// a concurrent test that enumerates the loaded assemblies holds every assembly object
    /// while it does, which would keep these alive through the collection rounds.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Release_RetainedType_KeepsDependencyThenCollects()
    {
        TestSkip.Unless(!OperatingSystem.IsBrowser(), "unloading needs CoreCLR");
        var (holderWeak, pointWeak) = RetainedScenario();
        Collect(() => !holderWeak.TryGetTarget(out _) && !pointWeak.TryGetTarget(out _));
        Assert.IsFalse(holderWeak.TryGetTarget(out _), "the retained definition should collect once it is dropped");
        Assert.IsFalse(pointWeak.TryGetTarget(out _), "its dependency should collect with it");
    }

    /// <summary>
    /// A resolver never finds a session type by name, even while an instance keeps it alive.
    /// </summary>
    [TestMethod]
    public void TypeResolver_DoesNotSeeSessionAssemblies()
    {
        var typeName = "SessionOnly" + Guid.NewGuid().ToString("N");
        var point = Images.LoadPoint(typeName);
        var pointType = point.Assembly.GetType(typeName)!;
        var resolver = new TypeResolver();
        Assert.Contains("not found", Assert.ThrowsExactly<ReplException>(() => resolver.Resolve(typeName, null)).Message);
        Assert.Contains("not found", Assert.ThrowsExactly<ReplException>(() => resolver.Resolve(typeName, point.Name)).Message);
        Assert.DoesNotContain(point.Assembly, resolver.Assemblies);
        GC.KeepAlive(pointType);
    }

    /// <summary>
    /// Excludes foreign collectible definitions while finding the same image when loaded through the resolver.
    /// </summary>
    /// <remarks>
    /// A resolver never finds a type in a collectible context it did not create, even by short
    /// name: a cell could not bind it, and the context may be unloading. The same image loaded
    /// through the resolver is found.
    /// </remarks>
    [TestMethod]
    public void TypeResolver_DoesNotSeeCollectibleContextsItDoesNotOwn()
    {
        var typeName = "Foreign" + Guid.NewGuid().ToString("N");
        var image = Images.Standalone(typeName);
        var context = new AssemblyLoadContext("foreign-" + typeName, isCollectible: true);
        try
        {
            var foreign = context.LoadFromStream(new MemoryStream(image));
            var resolver = new TypeResolver();
            Assert.Contains("not found", Assert.ThrowsExactly<ReplException>(() => resolver.Resolve(typeName, null)).Message);
            Assert.DoesNotContain(foreign, resolver.Assemblies);
            var own = resolver.LoadImage(image);
            Assert.AreNotSame(foreign, own);
            Assert.AreSame(own.GetType(typeName), resolver.Resolve(typeName, null));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Discovers assemblies loaded into the default context after an earlier lookup.
    /// </summary>
    [TestMethod]
    public void TypeResolver_SeesAnAssemblyLoadedAfterItsFirstSearch()
    {
        var typeName = "Later" + Guid.NewGuid().ToString("N");
        var resolver = new TypeResolver();
        Assert.Contains("not found", Assert.ThrowsExactly<ReplException>(() => resolver.Resolve(typeName, null)).Message);
        var loaded = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(Images.Standalone(typeName)));
        Assert.AreSame(loaded.GetType(typeName), resolver.Resolve(typeName, null));
        Assert.Contains(loaded, resolver.Assemblies);
    }

    /// <summary>
    /// The number of definitions one definition may reference is not limited: sixty-five
    /// families, each referencing every earlier one, all load and run.
    /// </summary>
    [TestMethod]
    public void Load_ManyMutualReferences_HasNoLimit()
    {
        var loaded = new List<DefinitionAssembly>();
        for (var i = 0; i < 65; i++)
        {
            loaded.Add(Images.LoadCounting(i, loaded));
        }

        var last = loaded[^1].Assembly.GetType("C64")!;
        Assert.AreEqual(64, last.GetMethod("Sum")!.Invoke(null, null));
    }

    /// <summary>
    /// A name typed into a cell resolves to a session type through the cell's own context.
    /// </summary>
    [TestMethod]
    public void Run_TypeGetTypeInCell_ResolvesSessionName()
    {
        var point = Images.LoadPoint();
        var session = new Session();
        session.AddLine($"ldstr \"Point, {point.Assembly.FullName}\"");
        session.AddLine("call class Type Type::GetType(string)");
        var value = session.Run().Value;
        Assert.AreSame(point.Assembly.GetType("Point"), value);
    }

    /// <summary>
    /// Cells are named uniquely and released after their run, so they do not accumulate.
    /// </summary>
    [TestMethod]
    public void Run_Cells_AreReleasedAfterRunning()
    {
        TestSkip.Unless(!OperatingSystem.IsBrowser(), "unloading needs CoreCLR");
        const int Runs = 20;
        var before = CellAssemblies();
        var session = new Session();
        for (var i = 0; i < Runs; i++)
        {
            session.AddLine("ldc.i4 " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.AreEqual(i, session.Run().Value);
        }

        var after = int.MaxValue;
        for (var round = 0; round < 20 && after - before >= Runs; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            after = CellAssemblies();
        }

        Assert.IsLessThan(before + Runs, after, "cell assemblies should be collected once their run returns");
    }

    private static int CellAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name?.StartsWith("ilrepl.cell.", StringComparison.Ordinal) == true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<Assembly> Holder, WeakReference<Assembly> Point) RetainedScenario()
    {
        var point = Images.LoadPoint();
        var holder = Images.LoadHolder(point);
        var holderType = holder.Assembly.GetType("Holder")!;
        var weak = (new WeakReference<Assembly>(holder.Assembly), new WeakReference<Assembly>(point.Assembly));
        SessionAssemblies.Release(holder);
        SessionAssemblies.Release(point);
        point = null!;
        holder = null!;
        Collect(() => false);
        Assert.IsTrue(weak.Item2.TryGetTarget(out _), "the dependency must stay alive while the referring type is retained");
        Assert.AreEqual(16, holderType.GetMethod("Read")!.Invoke(null, null), "first use after release must still resolve the dependency");
        return weak;
    }

    private static void Collect(Func<bool> done)
    {
        for (var round = 0; round < 12 && !done(); round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
