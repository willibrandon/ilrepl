using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Verifies real captured-image loading, preparation, and explicit execution boundaries inside native worker contexts.
/// </summary>
[TestClass]
public sealed class NativeWorkerContextTests
{
    /// <summary>
    /// An omitted nonframework image cannot be satisfied by an assembly already loaded in the parent process.
    /// </summary>
    [TestMethod]
    public void Context_UncapturedParentAssemblyIsRejected()
    {
        var method = typeof(NativeWorkerContextTests).GetMethod(nameof(Context_UncapturedParentAssemblyIsRejected))!;
        var target = new NativeTarget { Name = method.Name, Method = NativeCapture.Identify(method) };
        Assert.Contains(method.Module.Assembly, AssemblyLoadContext.Default.Assemblies);

        var error = Assert.ThrowsExactly<FileNotFoundException>(() =>
        {
            using var context = new NativeWorkerContext(target, new NativeOptions());
        });

        Assert.Contains("outside the captured native graph", error.ToString());
        Assert.Contains(method.Module.Assembly.GetName().Name!, error.ToString());
    }

    /// <summary>
    /// A reconstructed method preserves its IL and assembly identity while using a separate requested loading context.
    /// </summary>
    /// <param name="collectible">Whether to opt into collectible code generation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Context_PreservesImplementationInIsolatedLoadingContext(bool collectible)
    {
        var session = new Session { DeferActivation = true };
        try
        {
            Add(session, ".method int32 Value(int32 input) { ldarg.0; ldc.i4.2; mul; ret }");
            var original = session.Methods.Single().Version.Body;
            var options = new NativeOptions { Collectible = collectible };
            var target = NativeCapture.Create(session, "Value", options);

            using var context = new NativeWorkerContext(target, options);
            context.Prepare();

            Assert.AreEqual(original.Module.Assembly.FullName, context.Method.Module.Assembly.FullName);
            Assert.AreEqual(original.MetadataToken, context.Method.MetadataToken);
            Assert.AreNotSame(original.Module.Assembly, context.Method.Module.Assembly);
            Assert.AreNotSame(AssemblyLoadContext.GetLoadContext(original.Module.Assembly),
                AssemblyLoadContext.GetLoadContext(context.Method.Module.Assembly));
            Assert.AreEqual(collectible, context.Method.Module.Assembly.IsCollectible);
            Assert.AreSequenceEqual(original.GetMethodBody()!.GetILAsByteArray()!, context.Method.GetMethodBody()!.GetILAsByteArray()!);
            Assert.AreEqual(context.Method, context.InvocationMethod);
            Assert.IsEmpty(context.Arguments);
            Assert.IsTrue(context.Session.DeferActivation);
        }
        finally
        {
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    /// <summary>
    /// Preparation refuses direct or referenced module initializers by name until initialization is explicitly authorized.
    /// </summary>
    /// <param name="throughCaller">Whether the initializer belongs to a referenced callee's module.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Prepare_ModuleInitializerRequiresPermissionBeforeActivation(bool throughCaller)
    {
        using var files = new SessionWorkspaceFixture();
        var session = new Session { DeferActivation = true };
        try
        {
            var assembly = session.Resolver.LoadImage(ModuleInitializerFixture.Create(true, files.MarkerPath));
            if (throughCaller) Add(session, ".method int32 Caller() { call int32 Owner::Read(); ret }");
            var target = NativeCapture.Create(session, throughCaller ? "Caller" : "int32 Owner::Read()", new NativeOptions());
            using (var denied = new NativeWorkerContext(target, new NativeOptions()))
            {
                Assert.IsFalse(File.Exists(files.MarkerPath));
                var error = Assert.ThrowsExactly<ReplException>(denied.Prepare);
                Assert.Contains(assembly.FullName!, error.Message);
                Assert.Contains("--allow-initializers", error.Message);
                Assert.IsFalse(File.Exists(files.MarkerPath));
            }

            using var permitted = new NativeWorkerContext(target, new NativeOptions { AllowInitializers = true });
            Assert.IsFalse(File.Exists(files.MarkerPath));
            permitted.Prepare();

            Assert.AreEqual("initialized\n", File.ReadAllText(files.MarkerPath));
            Assert.IsEmpty(permitted.Arguments);
            Assert.IsTrue(permitted.Session.DeferActivation);
        }
        finally
        {
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    /// <summary>
    /// An unused captured dependency's initializer does not prevent preparing a body that cannot activate its module.
    /// </summary>
    /// <param name="cell">Whether unrelated session bindings retain the dependency during cell reconstruction.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Prepare_UnusedCapturedModuleInitializerDoesNotRequirePermission(bool cell)
    {
        using var files = new SessionWorkspaceFixture();
        var session = new Session { DeferActivation = true };
        try
        {
            var unused = session.Resolver.LoadImage(ModuleInitializerFixture.Create(true, files.MarkerPath));
            if (cell)
                Add(session, ".method int32 Unused() { call int32 Owner::Read(); ret }", "ldc.i4.s 42");
            else
                Add(session, ".class public Caller {",
                    ".method public static int32 Unused() { call int32 Owner::Read(); ret }",
                    ".method public static int32 Value() { ldc.i4.s 42; ret }", "}");
            var target = NativeCapture.Create(session, cell ? "" : "int32 Caller::Value()", new NativeOptions());
            Assert.Contains(assembly => assembly.Name == unused.FullName, target.Assemblies);
            using var context = new NativeWorkerContext(target, new NativeOptions());

            context.Prepare();

            Assert.AreEqual(cell ? "Run" : "Value", context.Method.Name);
            Assert.IsFalse(File.Exists(files.MarkerPath));
            Assert.IsEmpty(context.Arguments);
        }
        finally
        {
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    /// <summary>
    /// Static constructors and target bodies remain dormant during compilation and a denied invocation.
    /// </summary>
    [TestMethod]
    public async Task Prepare_StaticConstructorDoesNotAuthorizeBodyExecution()
    {
        using var files = new SessionWorkspaceFixture();
        var session = new Session { DeferActivation = true };
        try
        {
            Add(session, ".class public Owner {", ".field public static int32 Value", ".method static void .cctor() {",
                "ldstr " + LiteralParser.Escape(files.MarkerPath), "ldstr \"type initialized\\n\"",
                "call void File::AppendAllText(string, string)", "ret", "}",
                ".method public static int32 Read() { ldsfld int32 Owner::Value; ret }", "}");
            var options = new NativeOptions();
            var target = NativeCapture.Create(session, "int32 Owner::Read()", options);
            using var context = new NativeWorkerContext(target, options);

            context.Prepare();
            var error = await Assert.ThrowsExactlyAsync<ReplException>(context.InvokeAsync);

            Assert.Contains("requires literals, using Scenario, or --run", error.Message);
            Assert.IsFalse(File.Exists(files.MarkerPath));
            Assert.IsTrue(context.Session.DeferActivation);
            Assert.IsEmpty(context.Arguments);
        }
        finally
        {
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    /// <summary>
    /// Authorized workloads materialize literal arguments only at invocation and run exactly when the driver calls them.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_MaterializesArgumentsAndRunsOnlyExplicitInvocations()
    {
        using var files = new SessionWorkspaceFixture();
        var session = new Session { DeferActivation = true };
        try
        {
            Add(session, ".method void Write(string path) {", "ldarg.0", "ldstr \"body\\n\"",
                "call void File::AppendAllText(string, string)", "ret", "}");
            var options = new NativeOptions { Run = true, Arguments = [LiteralParser.Escape(files.MarkerPath)] };
            var target = NativeCapture.Create(session, "Write", options);
            using var context = new NativeWorkerContext(target, options);

            context.Prepare();
            Assert.IsEmpty(context.Arguments);
            Assert.IsFalse(File.Exists(files.MarkerPath));
            await context.InvokeAsync();

            Assert.AreEqual(files.MarkerPath, Assert.ContainsSingle(context.Arguments));
            Assert.AreEqual("body\n", File.ReadAllText(files.MarkerPath));
            Assert.IsFalse(context.Session.DeferActivation);

            await context.InvokeAsync();

            Assert.AreEqual("body\nbody\n", File.ReadAllText(files.MarkerPath));
            Assert.IsTrue(session.DeferActivation, "The live session must retain its own activation boundary.");
        }
        finally
        {
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    /// <summary>
    /// Constructor targets compile as their actual metadata bodies without requiring a receiver or executing construction.
    /// </summary>
    [TestMethod]
    public void Prepare_ConstructorCompilesWithoutCreatingInstance()
    {
        using var files = new SessionWorkspaceFixture();
        var session = new Session { DeferActivation = true };
        try
        {
            Add(session, ".class public Owner {", ".method public instance void .ctor() {", "ldarg.0",
                "call instance void object::.ctor()", "ldstr " + LiteralParser.Escape(files.MarkerPath), "ldstr \"constructed\"",
                "call void File::WriteAllText(string, string)", "ret", "}", "}");
            var target = NativeCapture.Create(session, "instance void Owner::.ctor()", new NativeOptions());
            using var context = new NativeWorkerContext(target, new NativeOptions());

            context.Prepare();

            Assert.IsInstanceOfType<ConstructorInfo>(context.Method);
            Assert.AreEqual(".ctor", context.Method.Name);
            Assert.IsFalse(File.Exists(files.MarkerPath));
            Assert.IsEmpty(context.Arguments);
        }
        finally
        {
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    /// <summary>
    /// A rejected captured graph restores the ambient session lifetime even when loading-context creation fails.
    /// </summary>
    [TestMethod]
    public void Context_FailedConstructionRestoresCallerLifetime()
    {
        var session = IlLines.Load(".method int32 Value() { ldc.i4.s 42; ret }", "ldc.i4.7");
        CompiledCell? compiled = null;
        try
        {
            var target = NativeCapture.Create(session, "Value", new NativeOptions());
            var duplicate = target with { Assemblies = [.. target.Assemblies, target.Assemblies[0]] };

            Assert.ThrowsExactly<ArgumentException>(() =>
            {
                using var rejected = new NativeWorkerContext(duplicate, new NativeOptions());
            });
            compiled = CellCompiler.CompileForInspection(session);

            Assert.IsTrue(compiled.Assembly.IsCollectible, "A failed worker setup leaked its noncollectible lifetime into the caller.");
            Assert.IsTrue(session.Methods.Single().Version.Body.Module.Assembly.IsCollectible);
        }
        finally
        {
            compiled?.Release();
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    private static void Add(Session session, params string[] source)
    {
        foreach (var line in IlLines.Expand(source)) session.AddLine(line);
    }
}
