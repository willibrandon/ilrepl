using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Reflected member contexts survive unchanged edits, revisions, comparisons, and independent exports.
/// </summary>
[TestClass]
public sealed class ReflectionDependencyTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Runtime member lookup retains private declarations and nested types without requiring a direct IL call to each member.
    /// </summary>
    /// <param name="lookup">The runtime reflection operation.</param>
    [TestMethod]
    [DataRow("runtime")]
    [DataRow("method")]
    [DataRow("enumeration")]
    [DataRow("property")]
    [DataRow("constructor")]
    [DataRow("nested")]
    public async Task Commit_ReflectionPreservesCompleteCopiedContext(string lookup)
    {
        var session = IlLines.Load(ReflectionDependencyExamples.Source(lookup).Split('\n'));
        var edit = session.PrepareEdit("int32 Owner::Read()", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        AssertContext(edit.Method.DeclaringType!);
        Assert.Contains(dependency => dependency.Symbol.Contains("Hidden", StringComparison.Ordinal)
            && dependency.Location.Contains("reflective access", StringComparison.Ordinal) && dependency.Disposition == "copied",
            edit.Dependencies);
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"), TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("43", result.Edited.Result!.Value);
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "reflected-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("reflection-export", isCollectible: true);
            try
            {
                var assembly = context.LoadImage(image);
                Assert.AreEqual(43, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                AssertContext(assembly.GetType(edit.Method.DeclaringType!.FullName!)!);
            }
            finally
            {
                context.Unload();
            }
        }

        var repeated = session.PrepareEdit("Copy", "Again");
        Assert.IsEmpty(repeated.Problems, string.Join('\n', repeated.Problems));
        session.CommitEdit(repeated.Name, repeated.Source);
        Assert.AreEqual(43, repeated.Method!.Invoke(null, null));
        AssertContext(repeated.Method.DeclaringType!);
    }

    private static void AssertContext(Type owner)
    {
        Assert.AreEqual(42, owner.GetMethod("Hidden", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null));
        var instance = Activator.CreateInstance(owner, nonPublic: true);
        Assert.AreEqual(42, owner.GetProperty("Value", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance));
        var nested = owner.GetNestedType("Nested", BindingFlags.NonPublic)!;
        Assert.IsNotNull(nested);
        Assert.AreSame(owner.Assembly, nested.Assembly);
        Assert.IsTrue(nested.IsNestedPrivate);
        Assert.AreEqual(42, nested.GetMethod("Read")!.Invoke(null, null));
    }
}
