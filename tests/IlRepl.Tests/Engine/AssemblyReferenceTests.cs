using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Real source metadata reference observations reject recoverably while ordinary identity and null operations remain supported.
/// </summary>
[TestClass]
public sealed class AssemblyReferenceTests
{
    /// <summary>
    /// Supplies cancellation for independently executing comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Enumerates bounded source metadata equality, hash, provenance and dispatch witnesses.
    /// </summary>
    public static IEnumerable<(string Target, string Operation, string Flow)> ReferenceCases => AssemblyReferenceFixture.Cases;

    /// <summary>
    /// Enumerates ordinary objects and strings, null comparisons and method-token controls.
    /// </summary>
    public static IEnumerable<(string Target, string Operation, string Flow)> SupportedReferenceCases =>
        AssemblyReferenceFixture.SupportedCases;

    /// <summary>
    /// Actual shared source identities execute before unchanged edits reject atomically and corrected revisions compare and export.
    /// </summary>
    /// <param name="target">The metadata object family.</param>
    /// <param name="operation">The identity observation.</param>
    /// <param name="flow">The provenance or invocation route.</param>
    [TestMethod]
    [DynamicData(nameof(ReferenceCases))]
    public async Task Edit_MetadataReferencesRetainRecoverableDraft(string target, string operation, string flow)
    {
        var image = AssemblyReferenceFixture.Create(target, operation, flow);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceIdentity(image, assembly);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]AssemblyReferences.Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        var problem = AssemblyReferenceFixture.Reason(operation, flow);
        Assert.Contains(item => item.Contains(problem, StringComparison.Ordinal), edit.Problems, string.Join("; ", edit.Problems));
        if (problem == AssemblyReferenceFixture.Problem && AssemblyReferenceFixture.Api(operation) is { } api)
        {
            Assert.Contains(item => item.Contains(api, StringComparison.Ordinal), edit.Problems);
            Assert.Contains(dependency => dependency.Symbol.Contains(api, StringComparison.Ordinal)
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
        session.CommitEdit(edit.Name, ".method public static int32 Read() {\nldc.i4.s 43\nret\n}");
        Assert.IsEmpty(edit.Problems);
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
        var previous = edit.Method;
        var corrected = edit.Source;
        completion = session.CompletionRevision;
        error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.Contains(problem, error.Message);
        Assert.AreSame(previous, edit.Method);
        Assert.AreEqual(corrected, edit.Source);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(completion, session.CompletionRevision);
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        AssertSourceIdentity(image, assembly);
        await AssertComparisonAsync(session, "Copy ()", 43);
        session.AddLine("call Copy");
        AssertExports(session, 43);
    }

    /// <summary>
    /// Ordinary identity, string equality, metadata null tests and standalone method tokens retain actual copied behavior.
    /// </summary>
    /// <param name="target">The ordinary or metadata value family.</param>
    /// <param name="operation">The supported comparison or token operation.</param>
    /// <param name="flow">The concrete ordinary, null or metadata-only route.</param>
    [TestMethod]
    [DynamicData(nameof(SupportedReferenceCases))]
    public async Task Edit_OrdinaryReferencesNullChecksAndTokensRemainSupported(string target, string operation, string flow)
    {
        var image = AssemblyReferenceFixture.Create(target, operation, flow);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceIdentity(image, assembly);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]AssemblyReferences.Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        await AssertComparisonAsync(session, "Copy ()", 42);
        session.AddLine("call Copy");
        AssertExports(session, 42);
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
    }

    private static void AssertSourceIdentity(byte[] image, Assembly assembly)
    {
        using var reader = new PEReader(new MemoryStream(image));
        var metadata = reader.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();
        var name = metadata.GetString(definition.Name);
        Assert.StartsWith("ReferenceSource", name);
        Assert.AreEqual(new Version(7, 8, 9, 10), definition.Version);
        Assert.AreEqual(name, assembly.GetName().Name);
        Assert.AreEqual(metadata.GetGuid(metadata.GetModuleDefinition().Mvid), assembly.ManifestModule.ModuleVersionId);
        var owner = assembly.GetType("AssemblyReferences.Owner", throwOnError: true)!;
        var sibling = assembly.GetType("AssemblyReferences.Sibling", throwOnError: true)!;
        Assert.AreNotEqual(owner, sibling);
        Assert.IsTrue(sibling.IsPublic);
        Assert.AreSame(assembly, owner.Assembly);
        Assert.AreSame(assembly, sibling.Assembly);
        Assert.AreSame(assembly.ManifestModule, owner.Module);
        Assert.AreSame(assembly.ManifestModule, sibling.Module);
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
