using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Reference-table inspection cannot silently observe generated copies while metadata tokens and user methods remain supported.
/// </summary>
[TestClass]
public sealed class ReferenceInspectionTests
{
    /// <summary>
    /// Supplies cancellation for real isolated comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Actual source references remain observable while unsupported edits reject atomically and corrected revisions compare and export.
    /// </summary>
    /// <param name="dispatch">The actual reference inspection or function-pointer binding form.</param>
    [TestMethod]
    [DataRow("direct")]
    [DataRow("ldftn")]
    [DataRow("virtual delegate")]
    [DataRow("invoke")]
    [DataRow("named delegate")]
    [DataRow("runtime named delegate")]
    public async Task Edit_ReferenceInspectionRetainsRecoverableDraft(string dispatch)
    {
        var image = ReferenceInspectionFixture.Create(dispatch);
        AssertUnusedReference(image);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceReference(assembly);
        var runtime = dispatch == "runtime named delegate";
        var arguments = runtime ? new object[] { nameof(Assembly.GetReferencedAssemblies) } : [];
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]ReferenceInspection.Owner::Read("
            + (runtime ? "string" : "") + ")", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, arguments));
        var problem = runtime ? "indirect reflection cannot prove a supported target" : ReferenceInspectionFixture.Problem;
        Assert.Contains(item => item.Contains(problem, StringComparison.Ordinal), edit.Problems, string.Join("; ", edit.Problems));
        if (!runtime)
        {
            Assert.Contains(item => item.Contains(nameof(Assembly.GetReferencedAssemblies), StringComparison.Ordinal), edit.Problems);
            Assert.Contains(dependency => dependency.Symbol.Contains(nameof(Assembly.GetReferencedAssemblies), StringComparison.Ordinal)
                && dependency.Disposition.Contains(problem, StringComparison.Ordinal), edit.Dependencies);
        }

        var source = edit.Source;
        var completion = session.CompletionRevision;
        var error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.Contains(problem, error.Message);
        Assert.AreEqual(source, edit.Source);
        Assert.AreEqual(0, edit.Revision);
        Assert.AreEqual(completion, session.CompletionRevision);
        Assert.IsNull(edit.Method);
        session.CommitEdit(edit.Name, ".method public static int32 Read(" + (runtime ? "string name" : "")
            + ") {\nldc.i4.s 43\nret\n}");
        Assert.IsEmpty(edit.Problems);
        Assert.AreEqual(43, edit.Method!.Invoke(null, arguments));
        var previous = edit.Method;
        var corrected = edit.Source;
        completion = session.CompletionRevision;
        error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.Contains(problem, error.Message);
        Assert.AreSame(previous, edit.Method);
        Assert.AreEqual(corrected, edit.Source);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(completion, session.CompletionRevision);
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, arguments));
        AssertSourceReference(assembly);
        await AssertComparisonAsync(session, runtime ? "Copy (\"GetReferencedAssemblies\")" : "Copy ()", 43);
        if (runtime) session.AddLine("ldstr \"GetReferencedAssemblies\"");
        session.AddLine("call Copy");
        AssertExports(session, 43);
    }

    /// <summary>
    /// A BCL method token and an identically named user method remain executable in copied contexts, real workers, and exported images.
    /// </summary>
    /// <param name="dispatch">The metadata-only reference or user-defined lookalike call.</param>
    [TestMethod]
    [DataRow("token")]
    [DataRow("lookalike")]
    public async Task Edit_MetadataAndUserLookalikesRemainSupported(string dispatch)
    {
        var image = ReferenceInspectionFixture.Create(dispatch);
        AssertUnusedReference(image);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceReference(assembly);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]ReferenceInspection.Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        await AssertComparisonAsync(session, "Copy ()", 42);
        session.AddLine("call Copy");
        AssertExports(session, 42);
    }

    private static void AssertUnusedReference(byte[] image)
    {
        using var reader = new PEReader(new MemoryStream(image));
        var metadata = reader.GetMetadataReader();
        var reference = metadata.AssemblyReferences.Single(handle =>
            metadata.GetString(metadata.GetAssemblyReference(handle).Name) == ReferenceInspectionFixture.UnusedReference);
        Assert.AreEqual(new Version(7, 8, 9, 10), metadata.GetAssemblyReference(reference).Version);
        Assert.DoesNotContain(handle => metadata.GetTypeReference(handle).ResolutionScope == reference, metadata.TypeReferences);
    }

    private static void AssertSourceReference(Assembly assembly)
    {
        var reference = Assert.ContainsSingle(assembly.GetReferencedAssemblies()
            .Where(reference => reference.Name == ReferenceInspectionFixture.UnusedReference));
        Assert.AreEqual(new Version(7, 8, 9, 10), reference.Version);
    }

    private async Task AssertComparisonAsync(Session session, string command, int expected)
    {
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, command), TestContext.CancellationToken);
        Assert.AreEqual(expected == 42 ? "match" : "different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.HasCount(1, side.Invocations);
        }

        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual(expected == 42 ? "42" : "43", result.Edited.Result!.Value);
    }

    private static void AssertExports(Session session, int expected)
    {
        foreach (var image in new[] { AssemblyExporter.Write(session, "reference-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("reference-copy", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(expected, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
