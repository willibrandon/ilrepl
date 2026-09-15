using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Copied member tokens reject recoverably while known external metadata, standalone tokens and user lookalikes remain supported.
/// </summary>
[TestClass]
public sealed class MemberTokenTests
{
    /// <summary>
    /// Supplies cancellation for independently executing comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Enumerates copied member token families and representative direct, indirect and unknown receiver routes.
    /// </summary>
    public static IEnumerable<(string Kind, string Dispatch)> MemberCases => MemberTokenFixture.Cases;

    /// <summary>
    /// Enumerates unchanged external members, standalone tokens, user lookalikes and ordinary member-name reflection.
    /// </summary>
    public static IEnumerable<(string Kind, string Dispatch)> SupportedMemberCases =>
        MemberTokenFixture.SupportedCases;

    /// <summary>
    /// Actual shifted source tokens execute before unchanged edits reject atomically and corrected revisions compare and export.
    /// </summary>
    /// <param name="kind">The reflected member family.</param>
    /// <param name="dispatch">The direct or indirect invocation route.</param>
    [TestMethod]
    [DynamicData(nameof(MemberCases))]
    public async Task Edit_MemberTokensRetainRecoverableDraft(string kind, string dispatch)
    {
        var image = MemberTokenFixture.Create(kind, dispatch);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceIdentity(image, assembly);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]MemberTokens.Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        var problem = MemberTokenFixture.Problem;
        Assert.Contains(item => item.Contains(problem, StringComparison.Ordinal), edit.Problems, string.Join("; ", edit.Problems));
        const string apiName = "MetadataToken";
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
        Assert.AreNotEqual(0x0200000a, edit.Method.DeclaringType!.MetadataToken);
        Assert.AreNotEqual(0x06000021, edit.Method.MetadataToken);
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
    /// Known external metadata and ordinary member reflection retain actual copied behavior.
    /// </summary>
    /// <param name="kind">The reflected member family.</param>
    /// <param name="dispatch">The standalone token or user lookalike.</param>
    [TestMethod]
    [DynamicData(nameof(SupportedMemberCases))]
    public async Task Edit_ExternalMemberTokensAndControlsRemainSupported(string kind, string dispatch)
    {
        var image = MemberTokenFixture.Create(kind, dispatch);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceIdentity(image, assembly);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]MemberTokens.Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        if (dispatch == "name")
        {
            var copied = edit.Method.DeclaringType!;
            Assert.AreNotEqual(0x0200000a, copied.MetadataToken);
            Assert.AreNotEqual(0x06000022, copied.GetMethod("Probe")!.MetadataToken);
            Assert.AreNotEqual(0x04000009, copied.GetField("Data")!.MetadataToken);
        }
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
        Assert.StartsWith("MemberTokenSource", metadata.GetString(definition.Name));
        Assert.AreEqual(new Version(7, 8, 9, 10), definition.Version);
        Assert.AreEqual(metadata.GetString(definition.Name), assembly.GetName().Name);
        Assert.AreEqual(metadata.GetGuid(metadata.GetModuleDefinition().Mvid), assembly.ManifestModule.ModuleVersionId);
        Assert.AreEqual("Owner", metadata.GetString(metadata.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(10)).Name));
        var genericOwner = assembly.GetType("MemberTokens.Owner+Hidden`1");
        Assert.HasCount(genericOwner is null ? 10 : 12, metadata.TypeDefinitions);
        if (genericOwner is not null)
        {
            Assert.AreEqual(0x0200000c, genericOwner.MetadataToken);
            Assert.IsTrue(genericOwner.IsNestedPrivate);
            Assert.IsTrue(genericOwner.IsGenericTypeDefinition);
            Assert.HasCount(1, genericOwner.GetGenericArguments());
            var parameter = genericOwner.GetGenericArguments()[0];
            Assert.AreEqual(0x2a000009, parameter.MetadataToken);
            Assert.AreEqual(genericOwner, parameter.DeclaringType);
            Assert.IsNull(parameter.DeclaringMethod);
            var paddingType = assembly.GetType("MemberTokens.GenericPadding`8", throwOnError: true)!;
            Assert.HasCount(8, paddingType.GetGenericArguments());
            Assert.AreEqual(0x2a000008, paddingType.GetGenericArguments()[7].MetadataToken);
            Assert.AreEqual(9, metadata.GetTableRowCount(TableIndex.GenericParam));
            var rawParameter = metadata.GetGenericParameter(MetadataTokens.GenericParameterHandle(9));
            Assert.AreEqual(MetadataTokens.TypeDefinitionHandle(12), (TypeDefinitionHandle)rawParameter.Parent);
            Assert.AreEqual(0, rawParameter.Index);
        }
        Assert.HasCount(9, metadata.FieldDefinitions);
        Assert.HasCount(9, metadata.PropertyDefinitions);
        Assert.HasCount(9, metadata.EventDefinitions);
        var owner = assembly.GetType("MemberTokens.Owner", throwOnError: true)!;
        Assert.AreEqual(0x0200000a, owner.MetadataToken);
        Assert.AreEqual(0x06000022, owner.GetMethod("Probe")!.MetadataToken);
        Assert.AreEqual(0x06000026, owner.GetConstructor(Type.EmptyTypes)!.MetadataToken);
        Assert.AreEqual(0x04000009, owner.GetField("Data")!.MetadataToken);
        Assert.AreEqual(0x17000009, owner.GetProperty("Value")!.MetadataToken);
        Assert.AreEqual(0x14000009, owner.GetEvent("Changed")!.MetadataToken);
        Assert.AreEqual(0x08000019, owner.GetMethod("Probe")!.GetParameters()[0].MetadataToken);
        var padding = assembly.GetType("MemberTokens.Padding7", throwOnError: true)!;
        Assert.AreEqual(0x02000009, padding.MetadataToken);
        Assert.AreEqual(0x04000008, padding.GetField("Data")!.MetadataToken);
        Assert.AreEqual(0x0600001d, padding.GetMethod("Probe")!.MetadataToken);
        var paddingMethod = padding.GetMethod("Probe")!;
        var ownerMethod = owner.GetMethod("Probe")!;
        if (ownerMethod.IsGenericMethodDefinition)
        {
            Assert.AreEqual(0x2a000008, paddingMethod.GetGenericArguments()[0].MetadataToken);
            Assert.AreEqual(0x2a000009, ownerMethod.GetGenericArguments()[0].MetadataToken);
            Assert.AreEqual(ownerMethod, ownerMethod.GetGenericArguments()[0].DeclaringMethod);
            paddingMethod = paddingMethod.MakeGenericMethod(typeof(int));
            ownerMethod = ownerMethod.MakeGenericMethod(typeof(int));
        }
        Assert.AreEqual(0x02000000, owner.MakeArrayType().MetadataToken);
        Assert.AreEqual(0x02000000, owner.MakeByRefType().MetadataToken);
        Assert.AreEqual(typeof(List<>).MetadataToken, typeof(List<int>).MetadataToken);
        Assert.AreEqual(typeof(List<>).MetadataToken, typeof(List<>).MakeGenericType(owner).MetadataToken);
        Assert.AreEqual(42, paddingMethod.Invoke(null, [1]));
        Assert.AreEqual(42, ownerMethod.Invoke(null, [1]));
        Assert.IsNotNull(Activator.CreateInstance(owner));
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
        foreach (var image in new[] { AssemblyExporter.Write(session, "member-token-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("member-token-copy", isCollectible: true);
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
