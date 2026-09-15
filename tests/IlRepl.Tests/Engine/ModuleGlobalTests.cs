using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Source module-global methods and fields execute before table inspection rejects recoverably; tokens and lookalikes remain supported.
/// </summary>
[TestClass]
public sealed class ModuleGlobalTests
{
    /// <summary>
    /// Supplies cancellation for independently executing comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Enumerates every source module global lookup overload and representative indirect invocation routes.
    /// </summary>
    public static IEnumerable<(string Api, string Dispatch)> GlobalCases => ModuleGlobalFixture.Cases;

    /// <summary>
    /// Enumerates standalone lookup tokens and user methods whose names resemble global lookup APIs.
    /// </summary>
    public static IEnumerable<(string Api, string Dispatch)> SupportedGlobalCases =>
        ModuleGlobalFixture.SupportedCases;

    /// <summary>
    /// Actual global members execute before unchanged edits reject atomically and corrected revisions compare and export.
    /// </summary>
    /// <param name="api">The global method or field lookup overload.</param>
    /// <param name="dispatch">The direct or indirect invocation route.</param>
    [TestMethod]
    [DynamicData(nameof(GlobalCases))]
    public async Task Edit_ModuleGlobalsRetainRecoverableDraft(string api, string dispatch)
    {
        var image = ModuleGlobalFixture.Create(api, dispatch);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceIdentity(image, assembly);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]ModuleGlobals.Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        var problem = ModuleGlobalFixture.Problem;
        Assert.Contains(item => item.Contains(problem, StringComparison.Ordinal), edit.Problems, string.Join("; ", edit.Problems));
        var apiName = ModuleGlobalFixture.ApiName(api);
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
    /// Standalone lookup tokens and user lookalikes retain actual copied behavior.
    /// </summary>
    /// <param name="api">The global member lookup spelling.</param>
    /// <param name="dispatch">The standalone token or user lookalike.</param>
    [TestMethod]
    [DynamicData(nameof(SupportedGlobalCases))]
    public async Task Edit_ModuleGlobalTokensAndLookalikesRemainSupported(string api, string dispatch)
    {
        var image = ModuleGlobalFixture.Create(api, dispatch);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceIdentity(image, assembly);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]ModuleGlobals.Owner::Read()", "Copy");
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
        Assert.StartsWith("GlobalSource", metadata.GetString(definition.Name));
        Assert.AreEqual(new Version(7, 8, 9, 10), definition.Version);
        Assert.AreEqual(metadata.GetString(definition.Name), assembly.GetName().Name);
        var global = metadata.GetTypeDefinition(metadata.TypeDefinitions.First());
        Assert.AreEqual("<Module>", metadata.GetString(global.Name));
        var methodRow = metadata.GetMethodDefinition(global.GetMethods().Single());
        Assert.AreEqual("GlobalValue", metadata.GetString(methodRow.Name));
        Assert.AreEqual(MethodAttributes.Public | MethodAttributes.Static, methodRow.Attributes);
        var fieldRow = metadata.GetFieldDefinition(global.GetFields().Single());
        Assert.AreEqual("GlobalData", metadata.GetString(fieldRow.Name));
        Assert.AreEqual(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA, fieldRow.Attributes);
        var data = reader.GetSectionData(fieldRow.GetRelativeVirtualAddress()).GetReader();
        Assert.AreEqual(42, data.ReadInt32());
        var module = assembly.ManifestModule;
        Assert.AreEqual(metadata.GetGuid(metadata.GetModuleDefinition().Mvid), module.ModuleVersionId);
        var method = module.GetMethods().Single();
        Assert.AreEqual("GlobalValue", method.Name);
        Assert.IsNull(method.DeclaringType);
        Assert.AreEqual(42, method.Invoke(null, null));
        Assert.AreEqual(42, module.GetMethod("GlobalValue")!.Invoke(null, null));
        var field = module.GetFields().Single();
        Assert.AreEqual("GlobalData", field.Name);
        Assert.IsNull(field.DeclaringType);
        Assert.AreEqual(42, field.GetValue(null));
        Assert.AreEqual(42, module.GetField("GlobalData")!.GetValue(null));
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
        foreach (var image in new[] { AssemblyExporter.Write(session, "global-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("global-copy", isCollectible: true);
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
