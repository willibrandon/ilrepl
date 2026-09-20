using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Private selected methods retain their metadata while their aliases remain callable and exportable.
/// </summary>
[TestClass]
public sealed class MethodEditPrivateTests
{
    /// <summary>
    /// The cancellation context for real worker executions.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A private static method remains private through revisions while ordinary callers and both exports use its alias.
    /// </summary>
    /// <param name="source">Whether to assemble the source independently with Microsoft ILAsm.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Alias_PrivateStaticMethodSupportsRevisionsAndExport(bool source)
    {
        var session = IlLines.Load(".class public Secret {",
            ".method private static int32 Read() { ldc.i4.1; ret }", "}");
        var edit = session.PrepareEdit("int32 Secret::Read()", "ReadSecret");
        session.CommitEdit(edit.Name, edit.Source);
        session.AddLine(".method int32 Caller() {");
        session.AddLine("call ReadSecret");
        session.AddLine("ret");
        session.AddLine("}");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal));
        Assert.IsTrue(edit.Method!.IsPrivate);
        Assert.AreEqual(1, edit.OriginalMethod.Invoke(null, null));
        session.AddLine("call Caller");
        Assert.AreEqual(2, session.Run().Value);
        session.AddLine("call ReadSecret");
        var image = source ? IlasmLocator.Assemble(session.ToIlAsm()) : AssemblyExporter.Write(session, "private-edit");
        var context = new AssemblyLoadContext("private-export", isCollectible: true);
        try
        {
            var assembly = context.LoadImage(image);
            var owner = assembly.GetType(edit.Method.DeclaringType!.FullName!)!;
            Assert.IsTrue(owner.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!.IsPrivate);
            Assert.AreEqual(2, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            Assert.AreEqual(2, assembly.GetType("IlRepl.Cell")!.GetMethod("Caller")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Direct and scenario comparisons record the actual private target through its callable entry.
    /// </summary>
    /// <param name="scenario">Whether the scenario calls the alias instead of the worker invoking the target directly.</param>
    /// <returns>The completed process assertions.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Compare_PrivateTargetRecordsBothImplementations(bool scenario)
    {
        var session = IlLines.Load(".class public Secret {",
            ".method private static int32 Read() { ldc.i4.1; ret }", "}");
        var edit = session.PrepareEdit("int32 Secret::Read()", "ReadSecret");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal));
        session.AddLine(".method int32 Scenario() {");
        session.AddLine("call ReadSecret");
        session.AddLine("ret");
        session.AddLine("}");
        var package = ComparisonCapture.Create(session, "ReadSecret " + (scenario ? "using Scenario" : "()"));
        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("1", result.Original.Result!.Value);
        Assert.AreEqual("2", result.Edited.Result!.Value);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
    }

    /// <summary>
    /// An instance alias consumes the copied receiver and retains the selected method's private metadata.
    /// </summary>
    [TestMethod]
    public void Alias_PrivateInstanceMethodRetainsReceiverAndExport()
    {
        var session = IlLines.Load(".class public Secret {",
            ".method public instance void .ctor() { ldarg.0; call instance void Object::.ctor(); ret }",
            ".method private instance int32 Read() { ldc.i4.s 42; ret }", "}");
        var edit = session.PrepareEdit("instance int32 Secret::Read()", "ReadSecret");
        session.CommitEdit(edit.Name, edit.Source);
        session.AddLine("newobj instance void IlRepl.Edits.ReadSecret.Owner::.ctor()");
        session.AddLine("call ReadSecret");
        Assert.AreEqual(42, session.Run().Value);
        Assert.IsTrue(edit.Method!.IsPrivate);
        Assert.IsFalse(edit.Method.IsStatic);
    }

    /// <summary>
    /// A private generic alias binds method arguments to its selected implementation.
    /// </summary>
    [TestMethod]
    public void Alias_PrivateGenericMethodUsesSelectedInstantiation()
    {
        var session = IlLines.Load(".class public Secret {",
            ".method private static !!T Read<T>(!!T value) { ldarg.0; ret }", "}");
        var edit = session.PrepareEdit("!!0 Secret::Read<int32>(!!0)", "ReadSecret");
        session.CommitEdit(edit.Name, edit.Source);
        session.AddLine("ldc.i4.s 42");
        session.AddLine("call ReadSecret");
        Assert.AreEqual(42, session.Run().Value);
        Assert.IsTrue(edit.Method!.IsPrivate);
        Assert.AreEqual(typeof(int), ((MethodInfo)edit.Method).GetGenericArguments().Single());
    }
}
