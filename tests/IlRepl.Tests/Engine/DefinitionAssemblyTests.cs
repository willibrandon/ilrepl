using System.Reflection;
using System.Runtime.CompilerServices;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for how session assemblies are named, loaded, recognized, kept alive, and released.
/// Each fact here was first established by a stage 1 spike against the runtime.
/// </summary>
[TestClass]
public sealed class DefinitionAssemblyTests
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

    /// <summary>
    /// Cecil-written images shaped like the ones the engine will write.
    /// </summary>
    private static class Images
    {
        public static DefinitionAssembly LoadPoint(string typeName = "Point")
        {
            var name = SessionAssemblies.NextName(SessionAssemblyKind.Types);
            var assembly = New(name);
            var module = assembly.MainModule;
            var point = new TypeDefinition("", typeName, TypeAttributes.Public | TypeAttributes.Class, module.ImportReference(typeof(object)));
            module.Types.Add(point);
            point.Methods.Add(Constructor(module));
            point.Methods.Add(Returning(module, "Value", MethodAttributes.Public | MethodAttributes.Static, 7));
            point.Methods.Add(Returning(module, "Secret", MethodAttributes.Assembly | MethodAttributes.Static, 9));
            return SessionAssemblies.Load(Write(assembly), name, SessionAssemblyKind.Types, []);
        }

        public static DefinitionAssembly LoadHolder(DefinitionAssembly point)
        {
            var name = SessionAssemblies.NextName(SessionAssemblyKind.Types);
            var assembly = New(name);
            var module = assembly.MainModule;
            IgnoreAccessChecksTo(module, point.Name);
            var reference = new AssemblyNameReference(point.Name, SessionAssemblies.Version);
            module.AssemblyReferences.Add(reference);
            var pointType = new TypeReference("", "Point", module, reference);
            var holder = new TypeDefinition("", "Holder", TypeAttributes.Public | TypeAttributes.Class, module.ImportReference(typeof(object)));
            module.Types.Add(holder);
            var read = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            var il = read.Body.GetILProcessor();
            il.Emit(OpCodes.Call, new MethodReference("Value", module.TypeSystem.Int32, pointType) { HasThis = false });
            il.Emit(OpCodes.Call, new MethodReference("Secret", module.TypeSystem.Int32, pointType) { HasThis = false });
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ret);
            holder.Methods.Add(read);
            var attribute = new CustomAttribute(module.ImportReference(typeof(System.Diagnostics.DebuggerTypeProxyAttribute).GetConstructor([typeof(Type)])!));
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(typeof(Type)), pointType));
            holder.CustomAttributes.Add(attribute);
            return SessionAssemblies.Load(Write(assembly), name, SessionAssemblyKind.Types, [point]);
        }

        public static DefinitionAssembly LoadCounting(int index, IReadOnlyList<DefinitionAssembly> earlier)
        {
            var name = SessionAssemblies.NextName(SessionAssemblyKind.Types);
            var assembly = New(name);
            var module = assembly.MainModule;
            var type = new TypeDefinition("", "C" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), TypeAttributes.Public | TypeAttributes.Class, module.ImportReference(typeof(object)));
            module.Types.Add(type);
            var sum = new MethodDefinition("Sum", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            var il = sum.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4_0);
            foreach (var dependency in earlier)
            {
                var reference = new AssemblyNameReference(dependency.Name, SessionAssemblies.Version);
                module.AssemblyReferences.Add(reference);
                var other = new TypeReference("", dependency.Assembly.GetTypes()[0].Name, module, reference);
                il.Emit(OpCodes.Call, new MethodReference("One", module.TypeSystem.Int32, other) { HasThis = false });
                il.Emit(OpCodes.Add);
            }

            il.Emit(OpCodes.Ret);
            type.Methods.Add(sum);
            type.Methods.Add(Returning(module, "One", MethodAttributes.Public | MethodAttributes.Static, 1));
            return SessionAssemblies.Load(Write(assembly), name, SessionAssemblyKind.Types, earlier);
        }

        private static AssemblyDefinition New(string name) =>
            AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, SessionAssemblies.Version), "M", ModuleKind.Dll);

        private static byte[] Write(AssemblyDefinition assembly)
        {
            using var stream = new MemoryStream();
            assembly.Write(stream);
            return stream.ToArray();
        }

        private static MethodDefinition Returning(ModuleDefinition module, string name, MethodAttributes attributes, int value)
        {
            var method = new MethodDefinition(name, attributes, module.TypeSystem.Int32);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4, value);
            il.Emit(OpCodes.Ret);
            return method;
        }

        private static MethodDefinition Constructor(ModuleDefinition module)
        {
            var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            var il = ctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            il.Emit(OpCodes.Ret);
            return ctor;
        }

        private static void IgnoreAccessChecksTo(ModuleDefinition module, string target)
        {
            var attribute = new TypeDefinition("System.Runtime.CompilerServices", "IgnoresAccessChecksToAttribute", TypeAttributes.Public | TypeAttributes.Class, module.ImportReference(typeof(Attribute)));
            var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            ctor.Parameters.Add(new ParameterDefinition("assemblyName", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.String));
            var il = ctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, module.ImportReference(typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!));
            il.Emit(OpCodes.Ret);
            attribute.Methods.Add(ctor);
            module.Types.Add(attribute);
            var applied = new CustomAttribute(ctor);
            applied.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, target));
            module.Assembly.CustomAttributes.Add(applied);
        }
    }
}
