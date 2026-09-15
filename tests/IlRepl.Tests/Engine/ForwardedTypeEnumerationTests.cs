using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Copies reject assembly forwarder enumeration before an unchanged method can observe a different type set.
/// </summary>
[TestClass]
public sealed class ForwardedTypeEnumerationTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A real forwarder remains visible to the original while the blocked copy can be replaced, compared, and exported.
    /// </summary>
    [TestMethod]
    public async Task Edit_ForwardedTypeEnumerationReportsItsSourceAndPreservesTheDraft()
    {
        var session = new Session();
        var assembly = session.Resolver.LoadImage(ForwardedTypeFixture.Create());
        Assert.AreSequenceEqual(new[] { typeof(string) }, assembly.GetForwardedTypes());
        var reference = "int32 [" + assembly.GetName().Name + "]Owner::Read()";
        var edit = session.PrepareEdit(reference, "Copy");
        Assert.AreEqual(1, edit.Original.Requested.Invoke(null, null));
        Assert.HasCount(1, edit.Problems);
        Assert.Contains("GetForwardedTypes", edit.Problems[0]);
        Assert.Contains("complete type set", edit.Problems[0]);
        Assert.Contains(dependency => dependency.Symbol.Contains("GetForwardedTypes", StringComparison.Ordinal)
            && dependency.Disposition.StartsWith("blocked:", StringComparison.Ordinal), edit.Dependencies);
        var source = edit.Source;
        Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.AreEqual(source, edit.Source);
        Assert.IsNull(edit.Method);
        session.CommitEdit(edit.Name, ".method public static int32 Read() {\nldc.i4.1\nret\n}");
        Assert.IsEmpty(edit.Problems);
        var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.AreEqual("1", comparison.Original.Result!.Value);
        Assert.AreEqual("1", comparison.Edited.Result!.Value);
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "forwarder-replacement"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("forwarder-replacement", isCollectible: true);
            try
            {
                var saved = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(1, saved.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
