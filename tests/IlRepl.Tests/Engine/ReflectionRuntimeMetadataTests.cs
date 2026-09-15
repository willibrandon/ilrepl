using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Reflection retains runtime declarations while actual IL references still require reproducible implementations.
/// </summary>
[TestClass]
public sealed class ReflectionRuntimeMetadataTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Member enumeration preserves non-IL declarations through unchanged edits, comparisons, and exports.
    /// </summary>
    /// <param name="virtualMethod">Whether the declaration is virtual.</param>
    /// <param name="runtime">Whether its code type is runtime.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Commit_ReflectionRetainsRuntimeMetadata(bool virtualMethod, bool runtime)
    {
        var session = new Session();
        session.Resolver.LoadImage(ReflectionRuntimeMetadataFixture.Create(virtualMethod, runtime));
        var edit = session.PrepareEdit("int32 Owner::Read()", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(2, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(2, edit.Method!.Invoke(null, null));
        AssertMetadata(edit.Method.DeclaringType!, virtualMethod, runtime);
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.AreEqual("2", comparison.Original.Result!.Value);
        Assert.AreEqual("3", comparison.Edited.Result!.Value);
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "runtime-metadata"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("runtime-metadata-export", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(3, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                AssertMetadata(assembly.GetType(edit.Method.DeclaringType!.FullName!)!, virtualMethod, runtime);
            }
            finally
            {
                context.Unload();
            }
        }

        var again = session.PrepareEdit("Copy", "Again");
        session.CommitEdit(again.Name, again.Source);
        Assert.AreEqual(3, again.Method!.Invoke(null, null));
        AssertMetadata(again.Method.DeclaringType!, virtualMethod, runtime);
    }

    /// <summary>
    /// IL references discovered before or after metadata retention still reject missing runtime implementations.
    /// </summary>
    /// <param name="call">The selected method or a subsequently discovered helper contains the reference.</param>
    [TestMethod]
    [DataRow("selected")]
    [DataRow("helper")]
    public void PrepareEdit_RuntimeCallRemainsBlocked(string call)
    {
        var session = new Session();
        session.Resolver.LoadImage(ReflectionRuntimeMetadataFixture.Create(false, false, call));
        var edit = session.PrepareEdit("int32 Owner::Read()", "Copy");
        Assert.Contains(problem => problem.Contains("Native", StringComparison.Ordinal)
            && problem.Contains("there is no IL", StringComparison.Ordinal), edit.Problems);
        var failure = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, edit.Source));
        Assert.Contains("Native", failure.Message);
        Assert.IsNull(edit.Method);
        Assert.AreEqual(0, edit.Revision);
    }

    private static void AssertMetadata(Type owner, bool virtualMethod, bool runtime)
    {
        var method = owner.GetMethod("Native", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!;
        Assert.IsNotNull(method);
        Assert.AreEqual(virtualMethod, method.IsVirtual);
        Assert.IsTrue(method.IsPrivate);
        Assert.AreEqual(MethodImplAttributes.InternalCall | (runtime ? MethodImplAttributes.Runtime : 0),
            method.GetMethodImplementationFlags());
        Assert.IsNull(method.GetMethodBody());
    }
}
