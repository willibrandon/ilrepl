using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Numeric source metadata tokens reject recoverably while standalone tokens, user lookalikes and ordinary member lookup remain supported.
/// </summary>
[TestClass]
public sealed class ModuleTokenTests
{
    /// <summary>
    /// Supplies cancellation for independently executing comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Enumerates every source module resolver overload and representative indirect invocation routes.
    /// </summary>
    public static IEnumerable<(string Target, string Api, string Dispatch)> TokenCases => ModuleTokenFixture.Cases;

    /// <summary>
    /// Enumerates standalone resolver tokens, user lookalikes and ordinary name-based member lookup.
    /// </summary>
    public static IEnumerable<(string Target, string Api, string Dispatch)> SupportedTokenCases =>
        ModuleTokenFixture.SupportedCases;

    /// <summary>
    /// Actual source token resolution executes before unchanged edits reject atomically and corrected revisions compare and export.
    /// </summary>
    /// <param name="target">The Module or ModuleHandle receiver.</param>
    /// <param name="api">The numeric token resolver.</param>
    /// <param name="dispatch">The direct or indirect invocation route.</param>
    [TestMethod]
    [DynamicData(nameof(TokenCases))]
    public async Task Edit_ModuleTokensRetainRecoverableDraft(string target, string api, string dispatch)
    {
        var image = ModuleTokenFixture.Create(target, api, dispatch);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceIdentity(image, assembly);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]ModuleTokens.Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        var problem = ModuleTokenFixture.Problem;
        Assert.Contains(item => item.Contains(problem, StringComparison.Ordinal), edit.Problems, string.Join("; ", edit.Problems));
        var apiName = ModuleTokenFixture.ApiName(api);
        Assert.Contains(item => item.Contains(apiName, StringComparison.Ordinal), edit.Problems);
        Assert.Contains(dependency => dependency.Symbol.Contains(apiName, StringComparison.Ordinal)
            && dependency.Disposition.Contains(problem, StringComparison.Ordinal), edit.Dependencies);
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
    /// Standalone method tokens, user resolvers and ordinary member names retain actual copied behavior.
    /// </summary>
    /// <param name="target">The BCL resolver family used as a token or lookalike.</param>
    /// <param name="api">The resolver API spelling.</param>
    /// <param name="dispatch">The standalone token, user lookalike or ordinary member lookup.</param>
    [TestMethod]
    [DynamicData(nameof(SupportedTokenCases))]
    public async Task Edit_ModuleTokenMetadataAndLookalikesRemainSupported(string target, string api, string dispatch)
    {
        var image = ModuleTokenFixture.Create(target, api, dispatch);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceIdentity(image, assembly);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]ModuleTokens.Owner::Read()", "Copy");
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
        Assert.StartsWith("TokenSource", metadata.GetString(definition.Name));
        Assert.AreEqual(new Version(7, 8, 9, 10), definition.Version);
        Assert.AreEqual(metadata.GetString(definition.Name), assembly.GetName().Name);
        var module = assembly.ManifestModule;
        Assert.AreEqual(metadata.GetGuid(metadata.GetModuleDefinition().Mvid), module.ModuleVersionId);
        Assert.AreEqual("HiddenMethod", module.ResolveMethod(0x06000002)!.Name);
        Assert.AreEqual("HiddenField", module.ResolveField(0x04000001)!.Name);
        Assert.AreEqual("HiddenType", module.ResolveType(0x02000003).Name);
        Assert.AreEqual("HiddenMethod", module.ResolveMember(0x06000002)!.Name);
        Assert.AreEqual("source token text", module.ResolveString(0x70000001));
        byte[] signature = [7, 1, 8];
        Assert.AreSequenceEqual(signature, module.ResolveSignature(0x11000001));
        Assert.AreEqual(42, module.ResolveMethod(0x06000002)!.Invoke(null, null));
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
        foreach (var image in new[] { AssemblyExporter.Write(session, "token-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("token-copy", isCollectible: true);
            context.Resolving += (_, name) => session.Resolver.Assemblies.FirstOrDefault(assembly => assembly.FullName == name.FullName);
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
