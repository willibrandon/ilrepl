using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Indirect invocation preserves recoverable drafts when the actual reflection target cannot be copied faithfully.
/// </summary>
[TestClass]
public sealed class IndirectReflectionTests
{
    /// <summary>
    /// Supplies cancellation for independent comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Supplies actual reflection targets and invocation overloads with independently executable original metadata.
    /// </summary>
    public static IEnumerable<(string Target, string Api, string Dispatch)> InvocationCases => IndirectReflectionFixture.Cases;

    /// <summary>
    /// Original reflection executes, unchanged edits fail atomically, and corrected revisions compare and export successfully.
    /// </summary>
    /// <param name="target">The metadata receiver.</param>
    /// <param name="api">The unsupported API reached indirectly.</param>
    /// <param name="dispatch">The invocation or binding family.</param>
    [TestMethod]
    [DynamicData(nameof(InvocationCases))]
    public async Task Edit_IndirectUnsupportedTargetsRetainRecoverableDraft(string target, string api, string dispatch)
    {
        await AssertRecoveryAsync(target, api, dispatch, IndirectReflectionFixture.Problem(api));
    }

    /// <summary>
    /// A caller-supplied member name executes the original but cannot establish a safe target during edit preflight.
    /// </summary>
    /// <param name="target">The assembly or module receiver.</param>
    [TestMethod]
    [DataRow("Assembly")]
    [DataRow("Module")]
    public async Task Edit_UnknownReflectionTargetIsBlocked(string target)
    {
        await AssertRecoveryAsync(target, "GetTypes", "unknown", "indirect reflection cannot prove a supported target");
    }

    /// <summary>
    /// Supported indirect overloads continue to invoke private copied methods and properties in independent comparison workers.
    /// </summary>
    /// <param name="dispatch">The supported invocation overload or factory.</param>
    [TestMethod]
    [DataRow("invoke options")]
    [DataRow("property index")]
    [DataRow("property options")]
    [DataRow("method delegate")]
    [DataRow("invoker")]
    [DataRow("function pointer")]
    public async Task Edit_KnownCopiedMembersRemainSupported(string dispatch)
    {
        var session = IlLines.Load(IndirectReflectionFixture.SupportedSource(dispatch).Split('\n'));
        var edit = session.PrepareEdit("int32 Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
    }

    /// <summary>
    /// A target changed through an aliased local or array, or selected by a callback, cannot be inferred from its earlier spelling.
    /// </summary>
    /// <param name="dispatch">The local or array alias, or custom callback, that changes the reflection target.</param>
    [TestMethod]
    [DataRow("address")]
    [DataRow("array alias")]
    [DataRow("custom resolver")]
    public async Task Edit_ReflectionTargetProvenanceCannotAssumeEarlierValues(string dispatch)
    {
        await AssertRecoveryAsync("Assembly", "GetTypes", dispatch, "indirect reflection cannot prove a supported target");
    }

    /// <summary>
    /// A safe literal delegate name remains supported when its receiver is supplied at runtime and the delegate is invoked dynamically.
    /// </summary>
    [TestMethod]
    public async Task Edit_RuntimeReceiverNamedDelegateRemainsSupportedThroughDynamicInvoke()
    {
        var session = IlLines.Load(IndirectReflectionFixture.SupportedSource("runtime delegate").Split('\n'));
        var edit = session.PrepareEdit("int32 Owner::Read(object)", "Copy");
        var original = Activator.CreateInstance(edit.Original.Requested.DeclaringType!);
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, [original]));
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, [Activator.CreateInstance(edit.Method.DeclaringType!)]));
        foreach (var line in new[] { ".method int32 Scenario() {", "newobj instance void IlRepl.Edits.Copy.Owner::.ctor()",
            "call Copy", "ret", "}" })
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
        session.AddLine("call Scenario");
        foreach (var image in new[] { AssemblyExporter.Write(session, "runtime-delegate"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("runtime-delegate", isCollectible: true);
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(42, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private async Task AssertRecoveryAsync(string target, string api, string dispatch, string problem)
    {
        var session = new Session();
        var assembly = session.Resolver.LoadImage(IndirectReflectionFixture.Create(target, api, dispatch));
        var unknown = dispatch == "unknown";
        var signature = unknown ? "string name" : "";
        var arguments = unknown ? new object[] { "GetTypes" } : [];
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]IndirectReflection.Owner::Read("
            + (unknown ? "string" : "") + ")", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, arguments));
        Assert.Contains(item => item.Contains(problem, StringComparison.Ordinal), edit.Problems,
            string.Join("; ", edit.Problems));
        if (!unknown && dispatch is not "address" and not "array alias" and not "custom resolver")
        {
            var apiName = api switch
            {
                "typed resource" => "GetManifestResourceStream", "typed attributes" => "GetCustomAttributes", _ => api,
            };

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
        session.CommitEdit(edit.Name, ".method public static int32 Read(" + signature + ") {\nldc.i4.s 42\nret\n}");
        Assert.IsEmpty(edit.Problems);
        Assert.AreEqual(42, edit.Method!.Invoke(null, arguments));
        var previous = edit.Method;
        var corrected = edit.Source;
        completion = session.CompletionRevision;
        error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.Contains(problem, error.Message);
        Assert.AreSame(previous, edit.Method);
        Assert.AreEqual(corrected, edit.Source);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(completion, session.CompletionRevision);
        var result = await ProcessComparisonRunner.RunAsync(
            ComparisonCapture.Create(session, unknown ? "Copy (\"GetTypes\")" : "Copy ()"), TestContext.CancellationToken);
        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.AreEqual("42", side.Result!.Value);
            Assert.HasCount(1, side.Invocations);
        }

        if (unknown)
        {
            session.AddLine("ldstr \"GetTypes\"");
        }

        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "indirect-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("indirect-copy", isCollectible: true);
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(42, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
