using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Real metadata references carried by arrays reject recoverably while ordinary values and null slots remain supported.
/// </summary>
[TestClass]
public sealed class ArrayReferenceTests
{
    /// <summary>
    /// Supplies cancellation for independently executing comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Enumerates array-carried metadata equality, hash, alias, helper, mutation and indirect witnesses.
    /// </summary>
    public static IEnumerable<(string Target, string Operation, string Flow)> ReferenceCases => ArrayReferenceFixture.Cases;

    /// <summary>
    /// Enumerates ordinary object and string arrays, null slots and slots distinct from metadata entries.
    /// </summary>
    public static IEnumerable<(string Target, string Operation, string Flow)> SupportedReferenceCases =>
        ArrayReferenceFixture.SupportedCases;

    /// <summary>
    /// Actual array-carried source identities execute before unchanged edits reject atomically and corrected revisions compare and export.
    /// </summary>
    /// <param name="target">The metadata object family.</param>
    /// <param name="operation">The identity observation.</param>
    /// <param name="flow">The provenance or invocation route.</param>
    [TestMethod]
    [DynamicData(nameof(ReferenceCases))]
    public async Task Edit_ArrayReferencesRetainRecoverableDraft(string target, string operation, string flow)
    {
        var image = ArrayReferenceFixture.Create(target, operation, flow);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceIdentity(image, assembly);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]ArrayReferences.Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        var problem = ArrayReferenceFixture.Reason(operation, flow);
        Assert.Contains(item => item.Contains(problem, StringComparison.Ordinal), edit.Problems, string.Join("; ", edit.Problems));
        if (problem == ArrayReferenceFixture.Problem && ArrayReferenceFixture.Api(operation) is { } api)
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
    /// Ordinary array identity, string equality and null slots retain copied behavior, including arrays with separate metadata slots.
    /// </summary>
    /// <param name="target">The ordinary or metadata value family.</param>
    /// <param name="operation">The supported comparison operation.</param>
    /// <param name="flow">The ordinary array, typed array or null slot route.</param>
    [TestMethod]
    [DynamicData(nameof(SupportedReferenceCases))]
    public async Task Edit_OrdinaryArrayElementsAndNullChecksRemainSupported(string target, string operation, string flow)
    {
        var image = ArrayReferenceFixture.Create(target, operation, flow);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceIdentity(image, assembly);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]ArrayReferences.Owner::Read()", "Copy");
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
        Assert.StartsWith("ArrayReferenceSource", name);
        Assert.AreEqual(new Version(7, 8, 9, 10), definition.Version);
        Assert.AreEqual(name, assembly.GetName().Name);
        Assert.AreEqual(metadata.GetGuid(metadata.GetModuleDefinition().Mvid), assembly.ManifestModule.ModuleVersionId);
        var owner = assembly.GetType("ArrayReferences.Owner", throwOnError: true)!;
        var sibling = assembly.GetType("ArrayReferences.Sibling", throwOnError: true)!;
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
        foreach (var image in new[] { AssemblyExporter.Write(session, "array-reference-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("array-reference-copy", isCollectible: true);
            try
            {
                var assembly = context.LoadImage(image);
                Assert.AreEqual(expected, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
