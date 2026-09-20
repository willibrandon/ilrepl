using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Copies and exports retain module initialization and agree with the real original assembly's initialized state.
/// </summary>
[TestClass]
public sealed class ModuleInitializerEditTests
{
    /// <summary>
    /// Supplies cancellation for isolated comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reading, revising, and exporting a copy do not run initialization; each executed revision initializes once.
    /// </summary>
    [TestMethod]
    public void Edit_ModuleInitializationWaitsForExecution()
    {
        var marker = Path.Join(Path.GetTempPath(), "ilrepl-module-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new Session();
            session.Resolver.LoadImage(ModuleInitializerFixture.Create(true, marker));
            Assert.IsFalse(File.Exists(marker), "loading the source ran its initializer");
            var edit = session.PrepareEdit("int32 Owner::Read()", "Copy");
            Assert.IsFalse(File.Exists(marker), "preparing the edit ran initialization");
            session.CommitEdit(edit.Name, edit.Source);
            Assert.IsFalse(File.Exists(marker), "committing the edit ran initialization");
            session.AddLine("call Copy");
            AssemblyExporter.Write(session, "module-initialization-capture");
            session.ToIlAsm();
            ComparisonCapture.Create(session, "Copy ()");
            Assert.IsFalse(File.Exists(marker));
            Assert.AreEqual(142, session.Run().Value);
            Assert.AreEqual("initialized\n", File.ReadAllText(marker));
            session.AddLine("call Copy");
            Assert.AreEqual(142, session.Run().Value);
            Assert.AreEqual("initialized\n", File.ReadAllText(marker));
            session.CommitEdit(edit.Name, edit.Source);
            Assert.AreEqual("initialized\n", File.ReadAllText(marker));
            session.AddLine("call Copy");
            Assert.AreEqual(142, session.Run().Value);
            Assert.AreEqual("initialized\ninitialized\n", File.ReadAllText(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    /// <summary>
    /// Separate edits from the same source module keep independently initialized state when combined into an export.
    /// </summary>
    /// <param name="fromCopy">Whether the second edit starts from the first committed copy.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Export_InitializesEachCopyIndependently(bool fromCopy)
    {
        var session = new Session();
        var source = session.Resolver.LoadImage(ModuleInitializerFixture.Create(true));
        foreach (var name in new[] { "First", "Second" })
        {
            session.PrepareEdit(fromCopy && name == "Second" ? "First" : "int32 [" + source.GetName().Name + "]Owner::Read()", name);
            session.CommitEdit(name, ModuleInitializerFixture.Method(name == "Second"));
        }

        session.AddLine("call First");
        session.AddLine("call Second");
        session.AddLine("add");
        foreach (var image in new[]
        {
            AssemblyExporter.Write(session, "module-initializer-copies"),
            IlasmLocator.Assemble(session.ToIlAsm()),
        })
        {
            var context = new AssemblyLoadContext("module-initializer-copies", isCollectible: true);
            try
            {
                var exported = context.LoadImage(image);
                var run = exported.GetType("IlRepl.Cell")!.GetMethod("Run")!;
                Assert.AreEqual(285, run.Invoke(null, null));
                Assert.AreEqual(285, run.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Unchanged and revised copies initialize their own fields once and preserve the declaring type's initialization order.
    /// </summary>
    /// <param name="typeInitializer">Whether the source type also has a static constructor.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Edit_PreservesModuleInitializationThroughComparisonAndExport(bool typeInitializer)
    {
        var session = new Session();
        var original = session.Resolver.LoadImage(ModuleInitializerFixture.Create(typeInitializer));
        Assert.AreEqual(142, original.GetType("Owner")!.GetMethod("Read")!.Invoke(null, null));
        var edit = session.PrepareEdit("int32 Owner::Read()", "Copy");
        Assert.IsEmpty(edit.Problems);
        session.CommitEdit(edit.Name, edit.Source);
        session.AddLine("call Copy");
        Assert.AreEqual(142, session.Run().Value);
        session.AddLine("call Copy");
        Assert.AreEqual(142, session.Run().Value);
        var unchanged = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", unchanged.Outcome, unchanged.Original.Detail + "; " + unchanged.Edited.Detail);
        Assert.AreEqual("142", unchanged.Original.Result!.Value);
        Assert.AreEqual("142", unchanged.Edited.Result!.Value);
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        session.AddLine("call Copy");
        Assert.AreEqual(143, session.Run().Value);
        var revised = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", revised.Outcome, revised.Original.Detail + "; " + revised.Edited.Detail);
        Assert.AreEqual("142", revised.Original.Result!.Value);
        Assert.AreEqual("143", revised.Edited.Result!.Value);
        session.AddLine("call Copy");
        foreach (var image in new[]
        {
            AssemblyExporter.Write(session, "module-initializer-export"),
            IlasmLocator.Assemble(session.ToIlAsm()),
        })
        {
            var context = new AssemblyLoadContext("module-initializer-export", isCollectible: true);
            try
            {
                var exported = context.LoadImage(image);
                Assert.DoesNotContain(reference => reference.Name == original.GetName().Name, exported.GetReferencedAssemblies());
                var run = exported.GetType("IlRepl.Cell")!.GetMethod("Run")!;
                Assert.AreEqual(143, run.Invoke(null, null));
                Assert.AreEqual(143, run.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
