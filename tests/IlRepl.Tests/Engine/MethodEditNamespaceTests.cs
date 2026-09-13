using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Reserved namespaces protect generated edit types from later session declarations.
/// </summary>
[TestClass]
public sealed class MethodEditNamespaceTests
{
    /// <summary>
    /// Supplies cancellation for real comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Declaring a generated owner or helper name fails before changing the session or its edit.
    /// </summary>
    /// <param name="committed">Whether the edit already has a callable revision.</param>
    /// <param name="name">The type name written in the rejected declaration.</param>
    [TestMethod]
    [DataRow(false, "IlRepl.Edits.Copy.Owner")]
    [DataRow(true, "IlRepl.Edits.Copy.Owner")]
    [DataRow(true, "'IlRepl.Edits.Copy.Owner'")]
    [DataRow(true, "IlRepl.Edits.Copy.Owner<T>")]
    [DataRow(true, "IlRepl.Edits.Copy.Type1")]
    [DataRow(true, "IlRepl.Edits.Copy.Helpers.Helper")]
    public void AddLine_GeneratedEditNamespace_IsReserved(bool committed, string name)
    {
        var session = CreateSession();
        var edit = session.PrepareEdit("instance int32 Counter::Read()", "Copy");
        var source = edit.Source.Replace("ldc.i4.s 41", "ldc.i4.s 42", StringComparison.Ordinal);
        if (committed)
        {
            session.CommitEdit(edit.Name, source);
        }

        var revision = session.CompletionRevision;
        var generation = session.Generation;
        var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine($".class public {name} {{"));

        Assert.AreEqual("the IlRepl namespace is reserved for the cell type", error.Message);
        Assert.IsNull(session.OpenType);
        Assert.IsNull(session.OpenMethod);
        Assert.HasCount(1, session.Types);
        Assert.AreEqual(revision, session.CompletionRevision);
        Assert.AreEqual(generation, session.Generation);
        Assert.AreEqual(committed ? 1 : 0, edit.Revision);
        if (!committed)
        {
            session.CommitEdit(edit.Name, source);
        }

        session.AddLine("newobj instance void IlRepl.Edits.Copy.Owner::.ctor()");
        session.AddLine("call Copy");
        Assert.AreEqual(42, session.Run().Value);
        session.AddLine("newobj instance void Counter::.ctor()");
        session.AddLine("call instance int32 Counter::Read()");
        Assert.AreEqual(41, session.Run().Value);
    }

    /// <summary>
    /// A reserved nested declaration leaves the enclosing declaration available to finish.
    /// </summary>
    [TestMethod]
    public void AddLine_ReservedNestedName_PreservesEnclosingDeclaration()
    {
        var session = CreateSession();
        var edit = session.PrepareEdit("instance int32 Counter::Read()", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        session.AddLine(".class public Container {");
        var open = session.OpenType;

        var error = Assert.ThrowsExactly<ReplException>(() =>
            session.AddLine(".class nested public 'IlRepl.Edits.Copy.Owner' {"));

        Assert.AreEqual("the IlRepl namespace is reserved for the cell type", error.Message);
        Assert.AreEqual(open, session.OpenType);
        session.AddLine(".class nested public Owner { }");
        session.AddLine("}");
        Assert.HasCount(2, session.Types);
        Assert.IsNotNull(session.Types.Single(type => type.FullName == "Container").RuntimeType!.GetNestedType("Owner"));
        session.AddLine("newobj instance void IlRepl.Edits.Copy.Owner::.ctor()");
        session.AddLine("call Copy");
        Assert.AreEqual(41, session.Run().Value);
    }

    /// <summary>
    /// Rejected owner declarations preserve instance scenarios and independently loaded exports.
    /// </summary>
    /// <param name="source">Whether Microsoft ILAsm independently assembles the exported source.</param>
    /// <returns>The completed worker and export assertions.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RejectedTypeDeclaration_PreservesInstanceComparisonAndExports(bool source)
    {
        var session = CreateSession();
        var edit = session.PrepareEdit("instance int32 Counter::Read()", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.s 41", "ldc.i4.s 42", StringComparison.Ordinal));
        Assert.ThrowsExactly<ReplException>(() => session.AddLine(".class public IlRepl.Edits.Copy.Owner { }"));
        foreach (var line in IlLines.Expand(".method int32 Scenario() {",
            "newobj instance void IlRepl.Edits.Copy.Owner::.ctor()", "call Copy", "ret", "}"))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("completed", result.Original.Outcome);
        Assert.AreEqual("completed", result.Edited.Outcome);
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);

        session.AddLine("call Scenario");
        var image = source ? IlasmLocator.Assemble(session.ToIlAsm()) : AssemblyExporter.Write(session, "edit-namespace");
        var context = new AssemblyLoadContext("edit-namespace-export", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            Assert.AreEqual(42, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            Assert.HasCount(1, assembly.GetTypes().Where(type => type.FullName == "IlRepl.Edits.Copy.Owner"));
            Assert.IsEmpty(assembly.GetReferencedAssemblies().Where(reference =>
                reference.Name!.StartsWith("ilrepl_", StringComparison.Ordinal)));
        }
        finally
        {
            context.Unload();
        }
    }

    private static Session CreateSession() => IlLines.Load(".class public Counter {",
        ".method public instance void .ctor() { ldarg.0; call instance void Object::.ctor(); ret }",
        ".method public instance int32 Read() { ldc.i4.s 41; ret }", "}");
}
