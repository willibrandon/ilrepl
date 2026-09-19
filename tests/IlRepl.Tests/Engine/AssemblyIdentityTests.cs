using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Assembly identity inspection retains recoverable edits without blocking ordinary metadata or user-defined lookalikes.
/// </summary>
[TestClass]
public sealed class AssemblyIdentityTests
{
    /// <summary>
    /// Supplies cancellation for real isolated comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Supplies bounded identity API, dispatch, and receiver cases from the shared real image fixture.
    /// </summary>
    public static IEnumerable<(string Api, string Dispatch, string Receiver)> IdentityCases => AssemblyIdentityFixture.Cases;

    /// <summary>
    /// Supplies metadata tokens and ordinary user, Type, and member operations that retain supported behavior.
    /// </summary>
    public static IEnumerable<(string Api, string Dispatch)> SupportedIdentityCases => AssemblyIdentityFixture.SupportedCases;

    /// <summary>
    /// Actual source identities remain observable while unsupported edits reject atomically and corrected revisions compare and export.
    /// </summary>
    /// <param name="api">The assembly identity API or property.</param>
    /// <param name="dispatch">The direct, reflection, or delegate binding form.</param>
    /// <param name="receiver">The executing assembly, owner type, or module receiver flow.</param>
    [TestMethod]
    [DynamicData(nameof(IdentityCases))]
    public async Task Edit_AssemblyIdentityRetainsRecoverableDraft(string api, string dispatch, string receiver)
    {
        var image = AssemblyIdentityFixture.Create(api, dispatch, receiver);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceIdentity(image, assembly);
        var runtime = dispatch == "runtime named delegate";
        var unproven = runtime || dispatch == "open method delegate";
        var arguments = runtime ? new object[] { "ToString" } : [];
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]AssemblyIdentity.Owner::Read("
            + (runtime ? "string" : "") + ")", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, arguments));
        var problem = unproven ? "indirect reflection cannot prove a supported target" : AssemblyIdentityFixture.Problem;
        Assert.Contains(item => item.Contains(problem, StringComparison.Ordinal), edit.Problems, string.Join("; ", edit.Problems));
        if (!unproven)
        {
            var apiName = api switch { "FullName" => "get_FullName", "GetName(bool)" => "GetName", _ => api };
            Assert.Contains(item => item.Contains(apiName, StringComparison.Ordinal), edit.Problems);
            Assert.Contains(dependency => dependency.Symbol.Contains(apiName, StringComparison.Ordinal)
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
        AssertSourceIdentity(image, assembly);
        await AssertComparisonAsync(session, runtime ? "Copy (\"ToString\")" : "Copy ()", 43);
        if (runtime)
        {
            session.AddLine("ldstr \"ToString\"");
        }

        session.AddLine("call Copy");
        AssertExports(session, 43);
    }

    /// <summary>
    /// Metadata tokens and ordinary user, Type, and member operations remain executable in copied contexts, workers, and exported images.
    /// </summary>
    /// <param name="api">The BCL or lookalike metadata API.</param>
    /// <param name="dispatch">The metadata token or ordinary user, Type, or member operation.</param>
    [TestMethod]
    [DynamicData(nameof(SupportedIdentityCases))]
    public async Task Edit_IdentityMetadataAndLookalikesRemainSupported(string api, string dispatch)
    {
        var image = AssemblyIdentityFixture.Create(api, dispatch);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(image);
        AssertSourceIdentity(image, assembly);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]AssemblyIdentity.Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        await AssertComparisonAsync(session, "Copy ()", 42);
        session.AddLine("call Copy");
        AssertExports(session, 42);
    }

    private static void AssertSourceIdentity(byte[] image, Assembly assembly)
    {
        using var reader = new PEReader(new MemoryStream(image));
        var metadata = reader.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();
        var name = metadata.GetString(definition.Name);
        Assert.StartsWith("IdentitySource", name);
        Assert.AreEqual(new Version(7, 8, 9, 10), definition.Version);
        Assert.AreEqual(name, assembly.GetName().Name);
        Assert.AreEqual(definition.Version, assembly.GetName(copiedName: true).Version);
        var expected = name + ", Version=7.8.9.10, Culture=neutral, PublicKeyToken=null";
        Assert.AreEqual(expected, assembly.FullName);
        Assert.AreEqual(expected, assembly.ToString());
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
        foreach (var image in new[] { AssemblyExporter.Write(session, "identity-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("identity-copy", isCollectible: true);
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
