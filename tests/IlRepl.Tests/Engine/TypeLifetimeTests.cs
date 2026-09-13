using System.Reflection;
using System.Runtime.CompilerServices;
using IlRepl.Engine;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// A session type is one runtime type for as long as the session keeps it: cells share it, its
/// statics keep their values, instances outlive the cell that made them, and a redefinition is a
/// new type that existing instances never move to.
/// </summary>
[TestClass]
public sealed class TypeLifetimeTests
{
    private static readonly string[] Counter =
    [
        ".class public Counter {",
        ".field public static int32 Count",
        ".field public int32 Id",
        ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); "
            + "ldarg.0; ldsfld int32 Counter::Count; ldc.i4 1; add; dup; stsfld int32 Counter::Count; "
            + "stfld int32 Counter::Id; ret }",
        "}",
    ];

    /// <summary>
    /// The test context, for its cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    private static Session Load(params string[] lines) => IlLines.Load(lines);

    private static object? Run(Session session, params string[] lines)
    {
        foreach (var line in IlLines.Expand(lines))
        {
            session.AddLine(line);
        }

        return session.Run().Value;
    }

    /// <summary>
    /// Loading a collectible definition leaves the process assembly catalog on the same snapshot.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void CollectibleLoad_DoesNotRebuildProcessAssemblyCatalog()
    {
        _ = DefineAndReset(new Session());
        var before = ProcessAssemblyCatalog();
        _ = DefineAndReset(new Session());
        var after = ProcessAssemblyCatalog();
        Assert.AreSame(before, after, "a collectible load must not make the next name search enumerate every loaded assembly");
    }

    /// <summary>
    /// A searchable load callback never waits for the gate used to publish the first catalog snapshot.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void SearchableLoad_DoesNotWaitForCatalogGate()
    {
        var type = typeof(TypeResolver).Assembly.GetType("IlRepl.Engine.ProcessAssemblies", throwOnError: true)!;
        var gate = (Lock)type.GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var bytes = SearchableAssembly();
        var load = Task.CompletedTask;
        gate.Enter();
        try
        {
            load = Task.Run(() => Assembly.Load(bytes));
            Assert.IsTrue(
                load.Wait(TimeSpan.FromSeconds(5), TestContext.CancellationToken),
                "the assembly-load callback must not wait for the catalog gate");
        }
        finally
        {
            gate.Exit();
            load.Wait(TestContext.CancellationToken);
        }
    }

    /// <summary>
    /// Checks reset releases definitions while concurrent threads continue resolving names.
    /// </summary>
    /// <remarks>
    /// A definition dropped by .reset collects while other threads keep loading definitions and
    /// resolving names, with and without a did-you-mean: a collectible load never makes a name
    /// search walk the runtime's assembly list and retain every collectible assembly it sees.
    /// Other tests capture process-wide assembly snapshots, so only this test's resolver workers may run alongside its collection checks.
    /// </remarks>
    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public void Reset_CollectsWhileOtherThreadsResolveNames()
    {
        var resolver = new TypeResolver();
        var context = new ParseContext([], [], GenericContext.Empty, resolver, []);
        var ct = TestContext.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var searches = new int[4];
        Task[] workers =
        [
            Task.Run(() => Repeat(() => resolver.Resolve("NoSuchTypeAnywhere", null), searches, 0, stop.Token), ct),
            Task.Run(() => Repeat(() => resolver.Resolve("NoSuchTypeAnywhere", null), searches, 1, stop.Token), ct),
            Task.Run(() => Repeat(() => TypeParser.Parse("Cosnole", context), searches, 2, stop.Token), ct),
            Task.Run(() => Repeat(() => _ = DefineAndReset(new Session()), searches, 3, stop.Token), ct),
        ];
        try
        {
            // The first miss scans every exported type once; the searches after it are the fast
            // ones that never leave the runtime's list alone.
            WaitForSearches(searches, ct);

            for (var round = 0; round < 5; round++)
            {
                var session = new Session();
                var weak = DefineAndReset(session);
                // Keep searches active after the collectible definition is loaded. Its load must
                // neither enter nor cause a rebuild of the process assembly catalog.
                WaitForSearches(searches, ct);
                for (var i = 0; i < 10 && weak.IsAlive; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                Assert.IsFalse(weak.IsAlive, $"round {round}: the dropped definition should collect while other threads resolve names");
            }
        }
        finally
        {
            stop.Cancel();
            Task.WaitAll(workers, ct);
        }
    }

    private static void WaitForSearches(int[] searches, CancellationToken cancellationToken)
    {
        var starts = Enumerable.Range(0, searches.Length).Select(index => Volatile.Read(ref searches[index])).ToArray();
        while (Enumerable.Range(0, searches.Length).Any(index => Volatile.Read(ref searches[index]) - starts[index] < 5))
        {
            Thread.Sleep(10);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void Repeat(Action miss, int[] searches, int index, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                miss();
            }
            catch (ReplException)
            {
                // The name is meant to miss; the search is what matters.
            }

            Interlocked.Increment(ref searches[index]);
        }
    }

    private static object ProcessAssemblyCatalog()
    {
        var type = typeof(TypeResolver).Assembly.GetType("IlRepl.Engine.ProcessAssemblies", throwOnError: true)!;
        return type.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    }

    private static byte[] SearchableAssembly()
    {
        using var definition = AssemblyDefinition.CreateAssembly(
            new("IlRepl.ProcessAssemblyProbe." + Guid.NewGuid().ToString("N"), new(1, 0)),
            "main",
            ModuleKind.Dll);
        using var stream = new MemoryStream();
        definition.Write(stream);
        return stream.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DefineAndReset(Session session)
    {
        foreach (var line in new[] { ".method int32 Two() {", "ldc.i4 2", "ret", "}" })
        {
            session.AddLine(line);
        }

        var weak = new WeakReference(session.Methods[0].Version.Body.Module.Assembly);
        session.Reset();
        return weak;
    }

    /// <summary>
    /// Two cells see the same type and the same static, and neither run recreates it.
    /// </summary>
    [TestMethod]
    public void Run_TwoCells_ShareTheTypeAndItsStatics()
    {
        var session = Load(Counter);
        var first = Run(session, "newobj instance void Counter::.ctor()");
        var second = Run(session, "newobj instance void Counter::.ctor()");
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreSame(first.GetType(), second.GetType());
        Assert.AreSame(session.Types[0].RuntimeType, first.GetType());
        Assert.AreEqual(2, Run(session, "ldsfld int32 Counter::Count"));
        Assert.AreEqual(1, first.GetType().GetField("Id")!.GetValue(first));
        Assert.AreEqual(2, second.GetType().GetField("Id")!.GetValue(second));
    }

    /// <summary>
    /// An instance stored in a static field is still the same object in a later cell.
    /// </summary>
    [TestMethod]
    public void Run_InstanceInAStatic_OutlivesTheCell()
    {
        var session = Load(
            ".class public Holder {",
            ".field public static object Kept",
            "}");
        Run(session, "ldstr \"kept\"", "stsfld object Holder::Kept");
        Assert.AreEqual("kept", Run(session, "ldsfld object Holder::Kept"));
        Assert.AreEqual("kept", session.Types[0].RuntimeType!.GetField("Kept")!.GetValue(null));
    }

    /// <summary>
    /// A type initializer runs once, when the type is first touched by a cell, and never again.
    /// </summary>
    [TestMethod]
    public void Run_TypeInitializer_RunsOnce()
    {
        var session = Load(
            ".class public Lazy {",
            ".field public static int32 Runs",
            ".field public static int32 Touched",
            ".method static void .cctor() { ldsfld int32 Lazy::Runs; ldc.i4 1; add; stsfld int32 Lazy::Runs; ret }",
            "}");
        Assert.AreEqual(1, Run(session, "ldsfld int32 Lazy::Runs"));
        Assert.AreEqual(1, Run(session, "ldsfld int32 Lazy::Runs"));
        Assert.AreEqual(1, Run(session, "ldsfld int32 Lazy::Touched", "pop", "ldsfld int32 Lazy::Runs"));
    }

    /// <summary>
    /// A redefinition is a new type; an instance of the old one keeps its type and its state.
    /// </summary>
    [TestMethod]
    public void Redefine_IsANewType_OldInstancesKeepTheirs()
    {
        var session = Load(Counter);
        var before = Run(session, "newobj instance void Counter::.ctor()")!;
        var oldType = session.Types[0].RuntimeType!;
        foreach (var line in IlLines.Expand(
            ".class public Counter {",
            ".field public static int32 Count",
            ".field public int32 Id",
            ".field public int32 Extra",
            "}"))
        {
            session.AddLine(line);
        }

        var newType = session.Types[0].RuntimeType!;
        Assert.AreNotSame(oldType, newType);
        Assert.AreSame(oldType, before.GetType());
        Assert.AreEqual(1, oldType.GetField("Id")!.GetValue(before));
        Assert.AreEqual(1, oldType.GetField("Count")!.GetValue(null), "the old type keeps its static");
        Assert.AreEqual(0, Run(session, "ldsfld int32 Counter::Count"), "the new type starts fresh");
        Assert.IsNotNull(newType.GetField("Extra"));
        Assert.AreEqual(1, session.TypeCount);
    }

    /// <summary>
    /// A class without a constructor has none, and newobj on it is refused with a hint.
    /// </summary>
    [TestMethod]
    public void ConstructorlessClass_HasNoConstructor()
    {
        var session = Load(".class public Bare {", ".field public static int32 X", "}");
        Assert.IsEmpty(session.Types[0].RuntimeType!.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Contains(
            "class Bare declares no constructor",
            Assert.ThrowsExactly<ReplException>(() => session.AddLine("newobj instance void Bare::.ctor()")).Message);
    }

    /// <summary>
    /// Clearing the cell keeps the types; resetting drops them and their assemblies can be collected.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Reset_DropsTypes_AndTheirAssembliesCollect()
    {
        var session = Load(Counter);
        session.AddLine("ldc.i4 1");
        session.ClearCell();
        Assert.AreEqual(1, session.TypeCount);
        var weak = Define(session);
        session.Reset();
        Assert.AreEqual(0, session.TypeCount);
        Assert.IsFalse(session.TypeTable.TryResolve("Counter", false, false, out _));
        for (var i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.IsFalse(weak.IsAlive, "the family's assembly should be collectible after reset");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Define(Session session) => new(session.Types[0].RuntimeType!.Assembly);

    /// <summary>
    /// An instance retained by the user keeps the old type alive after a reset.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Reset_RetainedInstance_KeepsItsTypeUsable()
    {
        var session = Load(Counter);
        var instance = Run(session, "newobj instance void Counter::.ctor()")!;
        session.Reset();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.AreEqual(1, instance.GetType().GetField("Id")!.GetValue(instance));
        Assert.AreEqual("Counter", instance.GetType().Name);
    }
}
