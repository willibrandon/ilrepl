using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// String-based activation creates the copied type through real execution, comparison workers, and exports.
/// </summary>
[TestClass]
public sealed partial class ActivationEditTests
{
    /// <summary>
    /// Supplies cancellation for isolated comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A second edit keeps both activation contexts independent when the aliases are exported together.
    /// </summary>
    [TestMethod]
    public void Export_CopyOfCopyKeepsActivationContexts()
    {
        var session = IlLines.Load(ActivationExamples.Source(2, true).Split('\n'));
        var first = session.PrepareEdit("int32 Activation.Owner::Read(string, string)", "First");
        session.CommitEdit(first.Name, first.Source);
        var second = session.PrepareEdit("First", "Second");
        Assert.IsEmpty(second.Problems, string.Join('\n', second.Problems));
        session.CommitEdit(second.Name, second.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        var name = ActivationExamples.Name(true, false);
        Assert.AreEqual(42, first.Method!.Invoke(null, [null, name]));
        Assert.AreEqual(43, second.Method!.Invoke(null, [null, name]));
        foreach (var alias in new[] { "First", "Second" })
        {
            session.AddLine("ldnull");
            session.AddLine("ldstr " + LiteralParser.Escape(name));
            session.AddLine("call " + alias);
        }

        session.AddLine("add");
        foreach (var image in new[] { AssemblyExporter.Write(session, "activation-aliases"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("activation-aliases", isCollectible: true);
            try
            {
                var saved = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(85, saved.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// File-based activation validates the source file and instantiates the copied owner through every string overload.
    /// </summary>
    /// <param name="overload">The number of activation parameters.</param>
    /// <param name="nested">Whether to instantiate a nested generic type.</param>
    [TestMethod]
    [DataRow(2, false)]
    [DataRow(2, true)]
    [DataRow(3, true)]
    [DataRow(8, false)]
    [DataRow(8, true)]
    public async Task Compare_FileActivationUsesCopiedTypes(int overload, bool nested)
    {
        var fixture = IlLines.Load(ActivationExamples.Source(overload, nested, true).Split('\n'));
        var original = fixture.PrepareEdit("int32 Activation.Owner::Read(string, string)", "Source").Original.Requested;
        Assert.IsTrue(SessionAssemblies.TryGetDefinition(original.Module.Assembly, out var definition));
        var path = Path.Combine(Path.GetTempPath(), definition.Name + ".dll");
        File.WriteAllBytes(path, definition.Image!);
        try
        {
            var session = new Session();
            var assembly = session.Resolver.Load(path);
            var name = ActivationExamples.Name(nested, overload == 8);
            Assert.AreEqual(42, assembly.GetType("Activation.Owner")!.GetMethod("Read")!.Invoke(null, [path, name]));
            var edit = session.PrepareEdit("int32 Activation.Owner::Read(string, string)", "Copy");
            Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
            session.CommitEdit(edit.Name, edit.Source);
            Assert.AreEqual(42, edit.Method!.Invoke(null, [path, name]));
            var arguments = "(" + LiteralParser.Escape(path) + ", " + LiteralParser.Escape(name) + ")";
            var unchanged = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy " + arguments),
                TestContext.CancellationToken);
            Assert.AreEqual("match", unchanged.Outcome, unchanged.Original.Detail + "; " + unchanged.Edited.Detail);
            Assert.AreEqual("42", unchanged.Original.Result!.Value);
            Assert.AreEqual("42", unchanged.Edited.Result!.Value);
            session.CommitEdit(edit.Name, ActivationExamples.Method(overload, nested, true, true));
            var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy " + arguments),
                TestContext.CancellationToken);
            Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
            Assert.AreEqual("42", changed.Original.Result!.Value);
            Assert.AreEqual("43", changed.Edited.Result!.Value);
            session.AddLine("ldstr " + LiteralParser.Escape(path));
            session.AddLine("ldstr " + LiteralParser.Escape(name));
            session.AddLine("call Copy");
            foreach (var image in new[] { AssemblyExporter.Write(session, "file-activation"), IlasmLocator.Assemble(session.ToIlAsm()) })
            {
                var context = new AssemblyLoadContext("file-activation", isCollectible: true);
                try
                {
                    var saved = context.LoadFromStream(new MemoryStream(image));
                    Assert.AreEqual(43, saved.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                }
                finally
                {
                    context.Unload();
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Assembly names, nested generics, constructor options, and unchanged caller strings survive copied activation calls.
    /// </summary>
    /// <param name="overload">The number of activation parameters.</param>
    /// <param name="nested">Whether to instantiate a nested generic type.</param>
    /// <param name="qualified">Whether to supply the original assembly's explicit identity.</param>
    [TestMethod]
    [DataRow(2, false, false)]
    [DataRow(2, false, true)]
    [DataRow(2, true, false)]
    [DataRow(2, true, true)]
    [DataRow(3, false, false)]
    [DataRow(3, true, true)]
    [DataRow(8, false, false)]
    [DataRow(8, false, true)]
    [DataRow(8, true, false)]
    [DataRow(8, true, true)]
    public async Task Compare_StringActivationUsesCopiedTypes(int overload, bool nested, bool qualified)
    {
        var session = IlLines.Load(ActivationExamples.Source(overload, nested).Split('\n'));
        var edit = session.PrepareEdit("int32 Activation.Owner::Read(string, string)", "Copy");
        var assembly = qualified ? edit.Original.Requested.DeclaringType!.Assembly.FullName : null;
        var name = ActivationExamples.Name(nested, overload == 8);
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, [assembly, name]));
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, [assembly, name]));
        var arguments = "(" + (assembly is null ? "null" : LiteralParser.Escape(assembly)) + ", " + LiteralParser.Escape(name) + ")";
        var unchanged = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy " + arguments),
            TestContext.CancellationToken);
        Assert.AreEqual("match", unchanged.Outcome, unchanged.Original.Detail + "; " + unchanged.Edited.Detail);
        Assert.AreEqual("42", unchanged.Original.Result!.Value);
        Assert.AreEqual("42", unchanged.Edited.Result!.Value);
        var output = string.Join(Environment.NewLine, "42", assembly, name, "");
        Assert.AreEqual(output, unchanged.Original.StandardOutput);
        Assert.AreEqual(output, unchanged.Edited.StandardOutput);
        session.CommitEdit(edit.Name, ActivationExamples.Method(overload, nested, true));
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy " + arguments),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        Assert.AreEqual("42", changed.Original.Result!.Value);
        Assert.AreEqual("43", changed.Edited.Result!.Value);
        Assert.AreEqual(output, changed.Edited.StandardOutput);
        session.AddLine(assembly is null ? "ldnull" : "ldstr " + LiteralParser.Escape(assembly));
        session.AddLine("ldstr " + LiteralParser.Escape(name));
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "activation-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("activation-copy", isCollectible: true);
            try
            {
                var saved = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(43, saved.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                Assert.DoesNotContain(reference => reference.Name == "IlRepl.Engine", saved.GetReferencedAssemblies());
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
